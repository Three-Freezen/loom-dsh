using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Restore;
using Loom.Core.Update;

namespace Loom.Core;

/// <summary>
/// Facade + orchestrator: update detection, snapshots, checks, file-level restore, reporting,
/// status for the tray/CLI. All write operations are serialized and journaled.
/// </summary>
public sealed class LoomService : ILoomService, IDisposable
{
    private readonly LoomPaths _paths;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly IInstallerLocator _locator;
    private readonly IProcessProbe _processes;
    private readonly ILockManager _locks;
    private readonly IUpdaterMonitor _updater;
    private readonly IProfileReader _profileReader;
    private readonly ISnapshotService _snapshots;
    private readonly ICheckService _checks;
    private readonly IRestoreService _restore;
    private readonly IReportService _reports;
    private readonly IDoctorService _doctor;
    private readonly IAutostartService _autostart;
    private readonly IOperationGate _operationGate;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _statusGate = new();
    private readonly object _stateGate = new();
    private readonly object _activityGate = new();
    private readonly List<ActivityItem> _activities = new();
    private readonly HashSet<string> _notifiedEventIds = new(StringComparer.Ordinal);
    private readonly object _notifiedGate = new();

    private LoomConfig _config;
    private readonly LoomState _state;
    private LoomStatus _status = new();
    private DshInstallation? _installation;
    private DateTimeOffset _installationResolvedAt = DateTimeOffset.MinValue;
    private bool _busy;
    private string? _busyLabel;
    private CancellationTokenSource? _currentCts;
    private DateTimeOffset? _appFirstSeenAt;
    private string? _checkedDshVersion;
    private CheckResult? _lastCheck;
    private bool _disposed;

    public LoomService(
        LoomPaths paths,
        LoomConfig config,
        LoomState state,
        ILogger logger,
        IClock clock,
        IInstallerLocator locator,
        IProcessProbe processes,
        ILockManager locks,
        IUpdaterMonitor updater,
        IProfileReader profileReader,
        ISnapshotService snapshots,
        ICheckService checks,
        IRestoreService restore,
        IReportService reports,
        IDoctorService doctor,
        IAutostartService autostart,
        IOperationGate operationGate)
    {
        _paths = paths;
        _config = config;
        _state = state;
        _logger = logger;
        _clock = clock;
        _locator = locator;
        _processes = processes;
        _locks = locks;
        _updater = updater;
        _profileReader = profileReader;
        _snapshots = snapshots;
        _checks = checks;
        _restore = restore;
        _reports = reports;
        _doctor = doctor;
        _autostart = autostart;
        _operationGate = operationGate;
        Updates = new UpdateService(_paths, () => _config, config =>
        {
            ConfigStore.Normalize(config);
            ConfigStore.Save(_paths, config);
        }, _logger);
        _paths.EnsureDirectories();
        _state.ToolVersion = LoomPaths.ToolVersion;
        _status = BuildStatus(null, false, null, new SnapshotStorageInfo());
    }

    public LoomPaths Paths => _paths;
    public LoomConfig Config => _config;
    public UpdateService Updates { get; }
    public bool IsBusy => _busy;

    /// <summary>
    /// Writes an exception to the service's own logger. The UI host must use this instead of opening a
    /// second <see cref="FileLogger"/> on the same file: two append handles at stale offsets would
    /// overwrite each other's lines (diagnostics for exactly the interesting failures got clobbered).
    /// </summary>
    public void LogException(Exception exception, string message) => _logger.LogException(exception, message);

    public event EventHandler<LoomStatus>? StatusChanged;
    public event EventHandler<LoomNotification>? NotificationRaised;

    public LoomStatus GetStatus()
    {
        lock (_statusGate)
        {
            return _status;
        }
    }

    // ---------------------------------------------------------------- status

    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        DshInstallation? installation;
        try
        {
            installation = await ResolveInstallationAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Installation resolution failed");
            installation = null;
        }

        var running = false;
        if (installation is not null)
        {
            try
            {
                running = _processes.FindDshProcesses(installation.InstallDir).Count > 0;
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Process probe failed");
            }
        }

        PendingInstallerInfo? pending = null;
        if (installation is not null)
        {
            try
            {
                pending = await _updater.FindPendingAsync(installation, installation.AppVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Updater cache probe failed");
            }
        }

        SnapshotStorageInfo storage;
        try
        {
            storage = _snapshots.InspectStorage(_paths);
        }
        catch
        {
            storage = new SnapshotStorageInfo();
        }

        var status = BuildStatus(installation, running, pending, storage);
        lock (_statusGate)
        {
            _status = status;
        }

        RaiseStatusChanged(status);
    }

    private void RaiseStatusChanged(LoomStatus? status = null)
    {
        status ??= GetStatus();
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "StatusChanged handler failed");
        }
    }

    private LoomStatus BuildStatus(
        DshInstallation? installation,
        bool appRunning,
        PendingInstallerInfo? pending,
        SnapshotStorageInfo storage)
    {
        LastOperation? lastOperation;
        SnapshotRef? lastSnapshot;
        SnapshotRef? preUpdate;
        LastCheckSummary? lastCheck;
        UpdatePhase phase;
        bool awaitingExit;
        lock (_stateGate)
        {
            lastOperation = _state.LastOperation;
            lastSnapshot = _state.LastSnapshot;
            preUpdate = _state.PreUpdateSnapshot;
            lastCheck = _state.LastCheck;
            phase = _state.Phase;
            awaitingExit = _state.AwaitingAppExit;
        }

        var status = new LoomStatus
        {
            Installation = installation?.ToInfo(),
            AppVersion = installation?.AppVersion,
            DshVersion = installation?.DshVersion,
            AppRunning = appRunning,
            Phase = phase,
            LastSnapshotAt = lastSnapshot?.TakenAt,
            LastSnapshotTrigger = lastSnapshot?.Trigger,
            ProtectedPlugins = lastSnapshot?.PluginCount ?? 0,
            SnapshotCount = storage.Count,
            SnapshotBytes = storage.TotalBytes,
            PendingIssues = lastCheck?.Issues ?? 0,
            IncompatiblePlugins = lastCheck?.Incompatible ?? 0,
            UnverifiedPlugins = lastCheck?.Unverified ?? 0,
            LastReportPath = lastOperation?.ReportPath ?? lastCheck?.ReportPath,
            FirstRun = !_config.FirstRunCompleted,
            Busy = _busy,
            BusyLabel = _busyLabel,
            RecentActivity = BuildActivity(lastOperation, lastCheck, lastSnapshot, preUpdate),
        };

        if (installation is null)
        {
            status.TrayState = TrayStatus.NotFound;
            status.Headline = "未找到 DSH 桌面版";
            status.Detail = "在设置中指定安装目录，或运行体检查看原因。";
            return status;
        }

        if (_busy)
        {
            status.TrayState = TrayStatus.Busy;
            status.Headline = _busyLabel ?? "正在处理";
            status.Detail = "完成前请勿关闭 Loom。";
            return status;
        }

        if (awaitingExit)
        {
            status.TrayState = TrayStatus.AwaitingExit;
            status.Headline = "需完全退出 DSH 后继续";
            status.Detail = "从 DSH 托盘菜单完全退出，然后点击“我已退出，重试”。";
            return status;
        }

        if (phase is UpdatePhase.Failed or UpdatePhase.VerifyFailed)
        {
            status.TrayState = TrayStatus.Failed;
            status.Headline = "上次操作失败";
            status.Detail = lastOperation?.Summary ?? "打开报告查看原因。";
            return status;
        }

        var lastCheckMatchesCurrent = lastCheck is not null
                                       && installation is not null
                                       && !VersionsDiffer(lastCheck.AppVersion, installation.AppVersion)
                                       && !VersionsDiffer(lastCheck.DshVersion, installation.DshVersion);
        if (phase == UpdatePhase.Blocked
            || (lastCheckMatchesCurrent && lastCheck!.Issues > 0 && phase != UpdatePhase.Done))
        {
            var issues = lastCheckMatchesCurrent ? lastCheck!.Issues : 0;
            status.PendingIssues = issues;
            status.IncompatiblePlugins = lastCheckMatchesCurrent ? lastCheck!.Incompatible : 0;
            status.UnverifiedPlugins = lastCheckMatchesCurrent ? lastCheck!.Unverified : 0;
            status.TrayState = TrayStatus.NeedsManual;
            status.Headline = issues > 0 ? $"{issues} 个插件需人工处理" : "插件需要人工处理";
            status.Detail = lastCheckMatchesCurrent ? lastCheck!.Summary : "打开报告查看兼容性结果。";
            return status;
        }

        if (!lastCheckMatchesCurrent)
        {
            status.PendingIssues = 0;
            status.IncompatiblePlugins = 0;
            status.UnverifiedPlugins = 0;
        }

        if (pending is not null)
        {
            status.TrayState = TrayStatus.AwaitingExit;
            status.Headline = $"检测到新版 {pending.TargetVersion}";
            status.Detail = "已保护当前插件状态，安装后 Loom 会自动检查并修复。";
            return status;
        }

        status.TrayState = TrayStatus.Normal;
        status.Headline = status.ProtectedPlugins > 0 ? $"已保护 {status.ProtectedPlugins} 个插件" : "已就绪";
        status.Detail = lastSnapshot is null
            ? "还没有快照，建议立即快照。"
            : $"最近快照 {lastSnapshot.TakenAt:MM-dd HH:mm}（应用 {lastSnapshot.AppVersion} / 运行时 {lastSnapshot.DshVersion}）。";
        return status;
    }

    private List<ActivityItem> BuildActivity(
        LastOperation? lastOperation,
        LastCheckSummary? lastCheck,
        SnapshotRef? lastSnapshot,
        SnapshotRef? preUpdate)
    {
        var items = new List<ActivityItem>();
        lock (_activityGate)
        {
            items.AddRange(_activities);
        }

        if (items.Count == 0)
        {
            if (lastOperation is not null)
            {
                items.Add(new ActivityItem
                {
                    At = lastOperation.At,
                    Title = KindLabel(lastOperation.Kind),
                    Detail = lastOperation.Summary,
                    Kind = lastOperation.Outcome == OperationOutcome.Succeeded ? "ok" : "warn",
                });
            }

            if (lastCheck is not null)
            {
                items.Add(new ActivityItem
                {
                    At = lastCheck.At,
                    Title = "兼容性检查",
                    Detail = lastCheck.Summary,
                    Kind = lastCheck.Issues > 0 ? "warn" : "ok",
                });
            }

            if (lastSnapshot is not null)
            {
                items.Add(new ActivityItem
                {
                    At = lastSnapshot.TakenAt,
                    Title = "快照",
                    Detail = $"{lastSnapshot.Trigger} · 应用 {lastSnapshot.AppVersion} / 运行时 {lastSnapshot.DshVersion}",
                    Kind = "ok",
                });
            }

            if (preUpdate is not null)
            {
                items.Add(new ActivityItem
                {
                    At = preUpdate.TakenAt,
                    Title = "更新前快照（冻结）",
                    Detail = $"应用 {preUpdate.AppVersion} / 运行时 {preUpdate.DshVersion}",
                    Kind = "ok",
                });
            }
        }

        return items
            .GroupBy(item => (item.Title, item.Detail))
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.At).First();
                var kind = latest.Kind;
                if (kind == "ok" && group.Any(item => item.Kind == "warn")) kind = "warn";
                return new ActivityItem { At = latest.At, Title = latest.Title, Detail = latest.Detail, Kind = kind };
            })
            .OrderByDescending(item => item.At)
            .Take(6)
            .ToList();
    }

    private void AddActivity(string title, string detail, string kind)
    {
        lock (_activityGate)
        {
            _activities.Insert(0, new ActivityItem
            {
                At = _clock.UtcNow,
                Title = title,
                Detail = detail,
                Kind = kind,
            });
            if (_activities.Count > 20) _activities.RemoveRange(20, _activities.Count - 20);
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "snapshot" => "快照",
        "check" => "兼容性检查",
        "restore" => "还原",
        "doctor" => "体检",
        _ => kind,
    };

    // ---------------------------------------------------------------- location

    public async Task<DshInstallation?> LocateAsync(CancellationToken cancellationToken = default)
        => await ResolveInstallationAsync(force: true, cancellationToken).ConfigureAwait(false);

    private async Task<DshInstallation?> ResolveInstallationAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (!force && _installation is not null && now - _installationResolvedAt < TimeSpan.FromSeconds(30))
        {
            return _installation;
        }

        if (force) _locator.Invalidate();
        _installation = await _locator.LocateAsync(_config, cancellationToken).ConfigureAwait(false);
        _installationResolvedAt = now;
        return _installation;
    }

    private async Task<DshInstallation> RequireInstallationAsync(CancellationToken cancellationToken)
    {
        var installation = await ResolveInstallationAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (installation is null || !installation.Found)
        {
            throw new LoomException(LoomErrorCodes.NotFound, "未找到 DeepSeek Harness 桌面版安装目录。");
        }

        return installation;
    }

    // ---------------------------------------------------------------- tick

    private int _tickRunning;

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        // The tray timer fires every 15s and a long check/full snapshot can exceed that:
        // serialize ticks so two of them can never run the update detection concurrently.
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;
        try
        {
            if (_busy) return;

            await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            var installation = _installation;
            if (installation is null || !installation.Found) return;

            var running = _status.AppRunning;
            if (!running)
            {
                _appFirstSeenAt = null;
                return;
            }

            _appFirstSeenAt ??= _clock.UtcNow;

            await DetectPendingUpdateAsync(installation, cancellationToken).ConfigureAwait(false);
            if (_busy) return;
            await DetectVersionChangeAsync(installation, cancellationToken).ConfigureAwait(false);
            if (_busy) return;
            await MaybeAutoSnapshotAsync(installation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    private async Task DetectPendingUpdateAsync(DshInstallation installation, CancellationToken cancellationToken)
    {
        var pending = await _updater.FindPendingAsync(installation, installation.AppVersion, cancellationToken)
            .ConfigureAwait(false);
        if (pending is null) return;

        if (SemVersion.TryParse(pending.TargetVersion, out var target)
            && SemVersion.TryParse(installation.AppVersion, out var current)
            && target <= current)
        {
            // stale download for the currently installed version (A04)
            return;
        }

        lock (_stateGate)
        {
            if (_state.HandledUpdate?.TargetVersion == pending.TargetVersion) return;
            if (_state.PendingUpdate?.TargetVersion == pending.TargetVersion
                && _state.PendingUpdate.Sha512 == pending.Sha512) return;

            _state.PendingUpdate = new PendingUpdate
            {
                TargetVersion = pending.TargetVersion,
                FileName = pending.FileName,
                Sha512 = pending.Sha512,
                DetectedAt = _clock.UtcNow,
                Source = pending.Stage,
                SnapshotTaken = false,
            };
            _state.Phase = UpdatePhase.UpdateDetected;
            StateStore.Save(_paths, _state);
        }

        _logger.Log(LogLevel.Info, $"Pending update detected: {pending.TargetVersion} ({pending.Stage})");
        AddActivity("检测到新版", $"目标版本 {pending.TargetVersion}，开始保护当前插件状态", "info");

        if (_config.FullSnapshotOnUpdate)
        {
            try
            {
                await TakeSnapshotAsync(
                    new SnapshotRequest { Trigger = SnapshotTrigger.PreUpdate, Full = true, Force = true },
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Pre-update snapshot failed");
            }
        }

        await RaiseNotificationAsync(
            $"update-detected-{pending.TargetVersion}",
            "检测到 DSH 新版",
            $"目标版本 {pending.TargetVersion}：已保存当前插件状态，更新后将自动检查兼容性。",
            NotificationKind.UpdateDetected,
            null,
            cancellationToken).ConfigureAwait(false);

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DetectVersionChangeAsync(DshInstallation installation, CancellationToken cancellationToken)
    {
        SnapshotRef? baselineRef;
        lock (_stateGate)
        {
            baselineRef = _state.PreUpdateSnapshot ?? _state.LastSnapshot;
        }

        if (baselineRef is null) return;

        var appChanged = VersionsDiffer(baselineRef.AppVersion, installation.AppVersion);
        var dshChanged = VersionsDiffer(baselineRef.DshVersion, installation.DshVersion);
        if (!appChanged && !dshChanged) return;

        if (!dshChanged)
        {
            // app-only update: no plugin compatibility work (A05)
            lock (_stateGate)
            {
                if (_state.HandledUpdate?.TargetVersion == installation.AppVersion) return;
                _state.HandledUpdate = new HandledUpdate
                {
                    TargetVersion = installation.AppVersion,
                    HandledAt = _clock.UtcNow,
                    Result = "app-only",
                };
                _state.LastSeenAppVersion = installation.AppVersion;
                _state.Phase = UpdatePhase.Done;
                StateStore.Save(_paths, _state);
            }

            try
            {
                await TakeSnapshotAsync(
                    new SnapshotRequest { Trigger = SnapshotTrigger.PostUpdate },
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Post-update snapshot failed");
            }

            AddActivity("应用已更新", "运行时版本未变化，插件无需处理", "ok");
            await RaiseNotificationAsync(
                $"app-updated-{installation.AppVersion}",
                "DSH 已更新",
                "运行时版本未变化，插件不受影响。",
                NotificationKind.Info,
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(_checkedDshVersion, installation.DshVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.Log(LogLevel.Info, $"DSH runtime changed to {installation.DshVersion}; running check");
        lock (_stateGate)
        {
            _state.Phase = UpdatePhase.NewVersionRunning;
            StateStore.Save(_paths, _state);
        }

        var outcome = await RunCheckAsync(new CheckRequest { Auto = true }, progress: null, cancellationToken)
            .ConfigureAwait(false);
        _checkedDshVersion = installation.DshVersion;
        if (!outcome.Success || outcome.Result is null) return;

        var result = outcome.Result;
        var actionableIssues = result.Issues.Count(issue => issue.Severity != Severity.Info);
        if (result.RestorePossible
            && result.Evidence == EvidenceLevel.Strong
            && _config.AutoRestoreAfterRecovery
            && !result.RequiresManualReview)
        {
            _logger.Log(LogLevel.Info, "Strong recovery evidence detected; attempting constrained file-level restore");
            await RestoreAsync(
                new RestoreRequest
                {
                    Auto = true,
                    // Automatic restore must be conservative: only evidence-backed candidates,
                    // no patch/compatibility merges and no user-disabled bundles (design 12.4).
                    Force = false,
                    EvidenceOverride = result.Evidence,
                    SnapshotId = result.Baseline?.Id,
                    AllowedBundles = result.RestoreCandidates,
                    AllowedDependencies = result.RestoreCandidates,
                    AllowPatchRestore = result.RestoreCandidates.Contains(DshDefaults.PatchFile, StringComparer.OrdinalIgnoreCase),
                    AllowCompatibilityRestore = false,
                },
                progress: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (actionableIssues > 0)
        {
            lock (_stateGate)
            {
                _state.Phase = UpdatePhase.Blocked;
                StateStore.Save(_paths, _state);
            }

            await RaiseNotificationAsync(
                $"check-issues-{installation.DshVersion}-{actionableIssues}",
                "更新后需要处理插件",
                result.Summary,
                NotificationKind.NeedsManual,
                outcome.Report?.MarkdownPath ?? outcome.Report?.JsonPath,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            lock (_stateGate)
            {
                _state.HandledUpdate = new HandledUpdate
                {
                    TargetVersion = installation.AppVersion,
                    HandledAt = _clock.UtcNow,
                    Result = "compatible",
                };
                _state.LastSeenAppVersion = installation.AppVersion;
                _state.LastSeenDshVersion = installation.DshVersion;
                _state.Phase = UpdatePhase.Done;
                StateStore.Save(_paths, _state);
            }

            try
            {
                await TakeSnapshotAsync(
                    new SnapshotRequest { Trigger = SnapshotTrigger.PostUpdate },
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogException(exception, "Baseline snapshot after update failed");
            }

            AddActivity("更新检查通过", "插件与新版运行时兼容", "ok");
            await RaiseNotificationAsync(
                $"check-ok-{installation.DshVersion}",
                "更新检查通过",
                "当前插件与新版本 DSH 兼容。",
                NotificationKind.Info,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MaybeAutoSnapshotAsync(DshInstallation installation, CancellationToken cancellationToken)
    {
        if (!_config.AutoSnapshotOnStartup || _appFirstSeenAt is null) return;
        if (_clock.UtcNow - _appFirstSeenAt.Value < TimeSpan.FromSeconds(_config.StartupSnapshotDelaySeconds)) return;

        SnapshotRef? lastSnapshot;
        DateTimeOffset? lastAuto;
        string? lastFingerprint;
        lock (_stateGate)
        {
            lastSnapshot = _state.LastSnapshot;
            lastAuto = _state.LastAutoSnapshotAt;
            lastFingerprint = _state.LastAutoSnapshotFingerprint;
            if (_state.PendingUpdate is not null || _state.Phase == UpdatePhase.Blocked) return;
        }

        if (lastAuto is not null
            && _clock.UtcNow - lastAuto.Value < TimeSpan.FromSeconds(_config.ProfileChangeDebounceSeconds))
        {
            return;
        }

        ProfileState profile;
        try
        {
            profile = _profileReader.Read(installation, strict: false);
        }
        catch
        {
            return;
        }

        var fingerprint = FingerprintOfProfile(profile);
        if (string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal)) return;
        if (lastSnapshot is not null
            && _clock.UtcNow - lastSnapshot.TakenAt < TimeSpan.FromSeconds(_config.ProfileChangeDebounceSeconds))
        {
            return;
        }

        await TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Auto }, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fingerprint of the live profile. Must stay structurally identical to
    /// <see cref="FingerprintOfManifest"/> so an automatic snapshot is not repeated on every tick.
    /// </summary>
    private static string FingerprintOfProfile(ProfileState profile) =>
        Hashing.Sha256HexOfText(string.Join("\n",
            string.IsNullOrEmpty(profile.PackageJsonText) ? "" : Hashing.Sha256HexOfText(profile.PackageJsonText),
            profile.LockfileSha256 ?? "",
            profile.PatchSha256 ?? "",
            string.Join("|", profile.Bundles)));

    // ---------------------------------------------------------------- snapshot

    public async Task<SnapshotOutcome> TakeSnapshotAsync(
        SnapshotRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var req = request ?? new SnapshotRequest();
        var outcome = new SnapshotOutcome();
        var scope = await BeginOperationAsync("snapshot", progress, cancellationToken).ConfigureAwait(false);
        outcome.OperationId = scope.OperationId;
        var started = scope.StartedAt;
        var warnings = new List<string>();
        var problems = new List<ReportProblem>();

        try
        {
            scope.Report(OperationStage.Locate, "定位 DSH 安装");
            var installation = await RequireInstallationAsync(scope.Token).ConfigureAwait(false);
            scope.Journal.Append(scope.OperationId, OperationStage.Locate, "done", installation.InstallDir);

            scope.Report(OperationStage.LockWait, "等待 profile 空闲");
            var wait = await _locks.WaitForQuietAsync(
                    installation,
                    TimeSpan.FromSeconds(_config.OperationTimeoutSeconds),
                    scope.Token)
                .ConfigureAwait(false);
            if (!wait.Quiet)
            {
                throw new LoomException(
                    LoomErrorCodes.Busy,
                    "插件管理器或 DSH 正在写入 profile：" + string.Join("；", wait.Status.Reasons));
            }

            scope.Report(OperationStage.Capture, req.Full ? "采集完整快照（含依赖闭包）" : "采集插件清单与配置");
            var manifest = await _snapshots.CaptureAsync(installation, req.Trigger, req.Full, scope.Token)
                .ConfigureAwait(false);
            scope.Journal.Append(
                scope.OperationId,
                OperationStage.Capture,
                "done",
                $"bundles={manifest.Profile.Bundles.Count} plugins={manifest.Plugins.Count} full={req.Full}");

            scope.Report(OperationStage.WriteSnapshot, "写入快照");
            var snapshotRef = await _snapshots.WriteAsync(manifest, _paths, _config, scope.Token)
                .ConfigureAwait(false);
            if (!_snapshots.Verify(snapshotRef))
            {
                throw new LoomException(LoomErrorCodes.CorruptSnapshot, "快照写入后校验失败。");
            }

            lock (_stateGate)
            {
                if (req.Trigger == SnapshotTrigger.PreUpdate)
                {
                    _state.PreUpdateSnapshot = snapshotRef;
                    if (_state.PendingUpdate is not null) _state.PendingUpdate.SnapshotTaken = true;
                    _state.Phase = UpdatePhase.SnapshotReady;
                }
                else
                {
                    _state.LastSnapshot = snapshotRef;
                    if (_state.Phase is UpdatePhase.Failed or UpdatePhase.VerifyFailed)
                    {
                        _state.Phase = UpdatePhase.Idle;
                    }
                }

                if (req.Trigger is SnapshotTrigger.Auto or SnapshotTrigger.PostUpdate or SnapshotTrigger.Manual)
                {
                    _state.LastAutoSnapshotAt = _clock.UtcNow;
                    _state.LastAutoSnapshotFingerprint = FingerprintOfManifest(manifest);
                }

                _state.LastOperation = new LastOperation
                {
                    Id = scope.OperationId,
                    Kind = "snapshot",
                    Outcome = OperationOutcome.Succeeded,
                    At = _clock.UtcNow,
                    Summary = $"快照 {snapshotRef.Id}（{manifest.Plugins.Count} 个插件）",
                };
                StateStore.Save(_paths, _state);
            }

            try
            {
                await _snapshots.RetainAsync(_paths, _state, _config, scope.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                warnings.Add("快照清理失败：" + exception.Message);
                _logger.LogException(exception, "Retention failed");
            }

            scope.Report(OperationStage.Report, "生成报告");
            outcome.Success = true;
            outcome.Skipped = false;
            outcome.Snapshot = snapshotRef;
            outcome.Warnings = warnings;
            outcome.Message = $"已创建快照 {snapshotRef.Id}";
            outcome.Report = await WriteReportAsync(
                scope.OperationId,
                "snapshot",
                started,
                OperationOutcome.Succeeded,
                outcome.Message,
                problems,
                new[]
                {
                    new ReportAction
                    {
                        Stage = "snapshot",
                        Description = $"快照 {snapshotRef.Id}",
                        Target = snapshotRef.Path,
                        Applied = true,
                    },
                },
                warnings,
                snapshotRef,
                EvidenceLevel.None,
                new JsonObject
                {
                    ["trigger"] = req.Trigger.ToString(),
                    ["full"] = req.Full,
                    ["plugins"] = manifest.Plugins.Count,
                    ["bundles"] = manifest.Profile.Bundles.Count,
                },
                scope.Token).ConfigureAwait(false);

            AddActivity("快照完成", $"{manifest.Plugins.Count} 个插件 · {req.Trigger}", "ok");
            scope.Journal.Append(scope.OperationId, OperationStage.Done, "done", outcome.Message);
        }
        catch (OperationCanceledException)
        {
            outcome.Success = false;
            outcome.Message = "操作已取消";
            scope.Journal.Append(scope.OperationId, OperationStage.Failed, "cancelled", outcome.Message);
        }
        catch (LoomException exception)
        {
            outcome.Success = false;
            outcome.ErrorCode = exception.Code;
            outcome.Message = exception.Message;
            problems.Add(new ReportProblem
            {
                Severity = Severity.Error,
                Code = exception.Code,
                Message = exception.Message,
            });
            scope.Journal.Append(scope.OperationId, OperationStage.Failed, "failed", outcome.Message);
            _logger.LogException(exception, "Snapshot failed", scope.OperationId);
        }
        catch (Exception exception)
        {
            outcome.Success = false;
            outcome.Message = "快照失败：" + exception.Message;
            problems.Add(new ReportProblem
            {
                Severity = Severity.Error,
                Code = LoomErrorCodes.Failed,
                Message = outcome.Message,
            });
            scope.Journal.Append(scope.OperationId, OperationStage.Failed, "failed", outcome.Message);
            _logger.LogException(exception, "Snapshot failed", scope.OperationId);
        }
        finally
        {
            scope.Dispose();
        }

        if (!outcome.Success)
        {
            RecordFailure(scope.OperationId, "snapshot", outcome.Message);
            outcome.Report = await SafeWriteReportAsync(
                scope.OperationId, "snapshot", started, OperationOutcome.Failed, outcome.Message, problems).ConfigureAwait(false);
        }

        await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome.Success && req.Trigger == SnapshotTrigger.PreUpdate)
        {
            await RaiseNotificationAsync(
                $"snapshot-preupdate-{outcome.Snapshot?.Id}",
                "已保存更新前快照",
                $"快照 {outcome.Snapshot?.Id} 已冻结，更新后可用于文件级还原。",
                NotificationKind.SnapshotTaken,
                outcome.Report?.MarkdownPath,
                CancellationToken.None).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>Same layout as <see cref="FingerprintOfProfile"/> (package sha, lock sha, patch sha, bundles).</summary>
    private static string FingerprintOfManifest(SnapshotManifest manifest) =>
        Hashing.Sha256HexOfText(string.Join("\n",
            manifest.Profile.FileSha256.TryGetValue(DshDefaults.ManifestFile, out var manifestHash) ? manifestHash : "",
            manifest.Profile.LockfileSha256 ?? "",
            manifest.Profile.Patch.Sha256 ?? "",
            string.Join("|", manifest.Profile.Bundles.Select(bundle => bundle.Name))));

    // ---------------------------------------------------------------- check

    public async Task<CheckOutcome> RunCheckAsync(
        CheckRequest? request = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = new CheckOutcome();
        var scope = await BeginOperationAsync("check", progress, cancellationToken).ConfigureAwait(false);
        outcome.OperationId = scope.OperationId;
        var started = scope.StartedAt;
        var problems = new List<ReportProblem>();

        try
        {
            scope.Report(OperationStage.Locate, "定位 DSH 安装");
            var installation = await RequireInstallationAsync(scope.Token).ConfigureAwait(false);

            SnapshotRef? baselineRef;
            List<string> warnings;
            lock (_stateGate)
            {
                baselineRef = _state.PreUpdateSnapshot ?? _state.LastSnapshot;
                warnings = new List<string>();
            }

            SnapshotManifest? baseline = null;
            if (baselineRef is not null)
            {
                baseline = await _snapshots.LoadAsync(baselineRef, scope.Token).ConfigureAwait(false);
                if (baseline is null)
                {
                    warnings.Add("基线快照内容缺失或损坏，按“无快照”处理。");
                }
            }

            var versions = new VersionDelta
            {
                AppVersionBefore = baselineRef?.AppVersion,
                AppVersionAfter = installation.AppVersion,
                DshVersionBefore = baselineRef?.DshVersion,
                DshVersionAfter = installation.DshVersion,
                AppChanged = VersionsDiffer(baselineRef?.AppVersion, installation.AppVersion),
                DshChanged = VersionsDiffer(baselineRef?.DshVersion, installation.DshVersion),
            };

            scope.Report(OperationStage.Check, "检查插件与运行时兼容性");
            var result = await _checks.CheckAsync(installation, baseline, baselineRef, versions, _paths, scope.Token)
                .ConfigureAwait(false);
            result.OperationId = scope.OperationId;
            _lastCheck = result;

            lock (_stateGate)
            {
                var actionableIssues = result.Issues.Count(issue => issue.Severity != Severity.Info);
                _state.LastCheck = new LastCheckSummary
                {
                    At = _clock.UtcNow,
                    AppVersion = installation.AppVersion,
                    DshVersion = installation.DshVersion,
                    Issues = actionableIssues,
                    Incompatible = result.IncompatibleCount,
                    Unverified = result.UnverifiedCount,
                    Unknown = result.UnknownCount,
                    Evidence = result.Evidence,
                    RestorePossible = result.RestorePossible,
                    Summary = result.Summary,
                };
                _state.Phase = result.RequiresManualReview
                    ? UpdatePhase.Blocked
                    : _state.Phase is UpdatePhase.Failed or UpdatePhase.VerifyFailed
                        ? UpdatePhase.Idle
                        : _state.Phase;
                _state.LastOperation = new LastOperation
                {
                    Id = scope.OperationId,
                    Kind = "check",
                    Outcome = OperationOutcome.Succeeded,
                    At = _clock.UtcNow,
                    Summary = result.Summary,
                };
                StateStore.Save(_paths, _state);
            }

            outcome.Success = true;
            outcome.Result = result;
            outcome.Message = result.Summary;
            outcome.Report = await WriteReportAsync(
                scope.OperationId,
                "check",
                started,
                OperationOutcome.Succeeded,
                result.Summary,
                result.Issues.Select(issue => new ReportProblem
                {
                    Severity = issue.Severity,
                    Code = issue.Code,
                    Message = issue.Message,
                    Subject = issue.Subject,
                    Detail = issue.Detail,
                }),
                result.RestoreCandidates.Select(candidate => new ReportAction
                {
                    Stage = "restore-candidate",
                    Description = "可文件级还原",
                    Target = candidate,
                }),
                warnings.Concat(result.Issues.Count == 0 ? Array.Empty<string>() : new[] { "存在需人工处理的插件问题。" }),
                baselineRef,
                result.Evidence,
                new JsonObject
                {
                    ["evidence"] = result.Evidence.ToString(),
                    ["issues"] = result.Issues.Count,
                    ["restorePossible"] = result.RestorePossible,
                    ["engine"] = result.CompatibilityEngine.ToString(),
                },
                scope.Token).ConfigureAwait(false);

            AddActivity("兼容性检查", result.Summary, result.Issues.Count > 0 ? "warn" : "ok");
            scope.Journal.Append(scope.OperationId, OperationStage.Done, "done", result.Summary);
        }
        catch (OperationCanceledException)
        {
            outcome.Success = false;
            outcome.Message = "操作已取消";
        }
        catch (LoomException exception)
        {
            outcome.Success = false;
            outcome.ErrorCode = exception.Code;
            outcome.Message = exception.Message;
            problems.Add(new ReportProblem { Severity = Severity.Error, Code = exception.Code, Message = exception.Message });
            _logger.LogException(exception, "Check failed", scope.OperationId);
        }
        catch (Exception exception)
        {
            outcome.Success = false;
            outcome.Message = "检查失败：" + exception.Message;
            problems.Add(new ReportProblem { Severity = Severity.Error, Code = LoomErrorCodes.Failed, Message = outcome.Message });
            _logger.LogException(exception, "Check failed", scope.OperationId);
        }
        finally
        {
            scope.Dispose();
        }

        if (!outcome.Success)
        {
            RecordFailure(scope.OperationId, "check", outcome.Message);
            scope.Journal.Append(
                scope.OperationId,
                OperationStage.Failed,
                "failed",
                outcome.Message);
            outcome.Report = await SafeWriteReportAsync(
                scope.OperationId, "check", started, OperationOutcome.Failed, outcome.Message, problems).ConfigureAwait(false);
        }

        await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    // ---------------------------------------------------------------- restore

    public async Task<RestorePlan?> PreviewRestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var installation = await RequireInstallationAsync(cancellationToken).ConfigureAwait(false);
        var snapshotRef = ResolveSnapshotRef(request.SnapshotId);
        if (snapshotRef is null) return null;
        var manifest = await _snapshots.LoadAsync(snapshotRef, cancellationToken).ConfigureAwait(false);
        if (manifest is null) return null;
        var current = _profileReader.Read(installation, strict: false);
        var constraints = new RestoreConstraints
        {
            AllowedBundles = request.AllowedBundles,
            AllowedDependencies = request.AllowedDependencies,
            AllowPatchRestore = request.AllowPatchRestore,
            AllowCompatibilityRestore = request.AllowCompatibilityRestore,
        };
        var plan = RestorePlanner.Create(manifest, current, installation, _config, constraints);
        plan.SnapshotDir = snapshotRef.Path;
        return plan;
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await BeginOperationAsync("restore", progress, cancellationToken).ConfigureAwait(false);
        var started = scope.StartedAt;
        var result = new RestoreResult
        {
            OperationId = scope.OperationId,
            Outcome = OperationOutcome.Failed,
            Plan = new RestorePlan(),
        };
        var problems = new List<ReportProblem>();
        var warnings = new List<string>();

        try
        {
            scope.Report(OperationStage.Locate, "定位 DSH 安装");
            var installation = await RequireInstallationAsync(scope.Token).ConfigureAwait(false);

            scope.Report(OperationStage.LockWait, "等待 profile 空闲");
            var quiet = await _locks.WaitForQuietAsync(
                    installation,
                    TimeSpan.FromSeconds(_config.OperationTimeoutSeconds),
                    scope.Token)
                .ConfigureAwait(false);
            if (!quiet.Quiet)
            {
                throw new LoomException(
                    LoomErrorCodes.Busy,
                    "插件管理器或 DSH 正在写入 profile：" + string.Join("；", quiet.Status.Reasons));
            }

            var snapshotRef = ResolveSnapshotRef(request.SnapshotId);
            if (snapshotRef is null)
            {
                throw new LoomException(LoomErrorCodes.NoSnapshot, "没有可用的快照，无法还原。");
            }

            scope.Report(OperationStage.ReadProfile, "读取基线快照");
            var manifest = await _snapshots.LoadAsync(snapshotRef, scope.Token).ConfigureAwait(false);
            if (manifest is null)
            {
                throw new LoomException(LoomErrorCodes.CorruptSnapshot, $"快照 {snapshotRef.Id} 缺失或校验失败。");
            }

            var current = _profileReader.Read(installation, strict: false);
            var constraints = new RestoreConstraints
            {
                AllowedBundles = request.AllowedBundles,
                AllowedDependencies = request.AllowedDependencies,
                AllowPatchRestore = request.AllowPatchRestore,
                AllowCompatibilityRestore = request.AllowCompatibilityRestore,
            };
            var plan = RestorePlanner.Create(manifest, current, installation, _config, constraints);
            plan.SnapshotDir = snapshotRef.Path;
            plan.SnapshotId = snapshotRef.Id;
            result.Plan = plan;

            if (request.DryRun)
            {
                result.Outcome = OperationOutcome.DryRun;
                result.Messages.Add($"计划 {plan.Actions.Count} 项操作，{plan.Conflicts.Count} 个冲突");
                result.RestartRequired = plan.HasChanges;
                await WriteReportAsync(
                    scope.OperationId, "restore", started, OperationOutcome.DryRun,
                    "还原演练：" + plan.Summary,
                    plan.Conflicts.Select(conflict => new ReportProblem
                    {
                        Severity = Severity.Warning,
                        Code = "E_CONFLICT",
                        Message = conflict.Reason,
                        Subject = conflict.Subject,
                    }),
                    plan.Actions.Select(action => new ReportAction
                    {
                        Stage = action.Kind.ToString(),
                        Description = action.Description,
                        Target = action.TargetFile,
                    }),
                    null, snapshotRef, _lastCheck?.Evidence ?? EvidenceLevel.None, null, scope.Token)
                    .ConfigureAwait(false);
                scope.Journal.Append(scope.OperationId, OperationStage.Done, "done", "dry-run: no files written");
                return result;
            }

            if (plan.RequiresManualReview && !request.Force)
            {
                throw new LoomException(
                    LoomErrorCodes.NeedsManual,
                    "还原涉及冲突或缺少强证据，需要人工确认（可加 --force 或从界面确认）。");
            }

            var evidence = request.EvidenceOverride ?? _lastCheck?.Evidence ?? EvidenceLevel.None;
            if (request.Auto && evidence != EvidenceLevel.Strong)
            {
                throw new LoomException(LoomErrorCodes.RestoreRejected, "证据不足，未自动还原。");
            }

            scope.Report(OperationStage.Apply, "应用文件级还原");
            using var manifestLock = await _locks.AcquireManifestLockAsync(
                    installation,
                    TimeSpan.FromSeconds(Math.Min(30, _config.OperationTimeoutSeconds)),
                    scope.Token)
                .ConfigureAwait(false);
            if (manifestLock is null)
            {
                throw new LoomException(LoomErrorCodes.Busy, "无法获取 package.json 锁，请稍后重试。");
            }

            var applyResult = await _restore.ApplyAsync(
                new RestoreApplyRequest
                {
                    Installation = installation,
                    Paths = _paths,
                    Plan = plan,
                    OperationId = scope.OperationId,
                    Evidence = evidence,
                    Snapshot = manifest,
                },
                scope.Token).ConfigureAwait(false);

            result = applyResult;
            result.OperationId = scope.OperationId;
            result.Plan = plan;

            if (result.Outcome == OperationOutcome.Succeeded)
            {
                lock (_stateGate)
                {
                    _state.Phase = UpdatePhase.Done;
                    _state.HandledUpdate = new HandledUpdate
                    {
                        TargetVersion = installation.AppVersion,
                        HandledAt = _clock.UtcNow,
                        Result = "file-restore",
                    };
                    _state.LastSeenAppVersion = installation.AppVersion;
                    _state.LastSeenDshVersion = installation.DshVersion;
                    _state.PendingUpdate = null;
                    _state.AwaitingAppExit = false;
                    _state.PendingRestoreSnapshotId = null;
                    _state.LastOperation = new LastOperation
                    {
                        Id = scope.OperationId,
                        Kind = "restore",
                        Outcome = OperationOutcome.Succeeded,
                        At = _clock.UtcNow,
                        Summary = result.Messages.FirstOrDefault() ?? "文件级还原完成",
                    };
                    StateStore.Save(_paths, _state);
                }

                AddActivity("还原完成", $"{result.RestoredCount} 项已还原，{result.SkippedCount} 项跳过", "ok");
                await RaiseNotificationAsync(
                    $"restore-{scope.OperationId}",
                    "插件还原完成",
                    $"已还原 {result.RestoredCount} 项；重启 DSH 后生效。",
                    NotificationKind.RestoreCompleted,
                    result.ReportPath,
                    scope.Token).ConfigureAwait(false);
            }
            else
            {
                warnings.AddRange(result.Messages);
            }

            result.ReportPath = (await WriteReportAsync(
                scope.OperationId, "restore", started, result.Outcome,
                result.Messages.FirstOrDefault() ?? plan.Summary,
                problems,
                plan.Actions.Select(action => new ReportAction
                {
                    Stage = action.Kind.ToString(),
                    Description = action.Description,
                    Target = action.TargetFile,
                    Applied = result.Outcome == OperationOutcome.Succeeded,
                }),
                warnings,
                snapshotRef,
                evidence,
                new JsonObject
                {
                    ["restored"] = result.RestoredCount,
                    ["skipped"] = result.SkippedCount,
                    ["conflicts"] = result.ConflictCount,
                },
                scope.Token).ConfigureAwait(false))?.MarkdownPath;

            scope.Journal.Append(scope.OperationId, OperationStage.Done, "done", "restore finished");
        }
        catch (OperationCanceledException)
        {
            result.Outcome = OperationOutcome.Cancelled;
            result.Messages.Add("操作已取消");
        }
        catch (LoomException exception)
        {
            result.Outcome = exception.Code == LoomErrorCodes.NeedsManual
                ? OperationOutcome.NeedsManual
                : OperationOutcome.Failed;
            result.ErrorCode = exception.Code;
            result.Messages.Add(exception.Message);
            problems.Add(new ReportProblem { Severity = Severity.Warning, Code = exception.Code, Message = exception.Message });
            _logger.LogException(exception, "Restore failed", scope.OperationId);
        }
        catch (Exception exception)
        {
            result.Outcome = OperationOutcome.Failed;
            result.Messages.Add("还原失败：" + exception.Message);
            problems.Add(new ReportProblem { Severity = Severity.Error, Code = LoomErrorCodes.Failed, Message = exception.Message });
            _logger.LogException(exception, "Restore failed", scope.OperationId);
        }
        finally
        {
            scope.Dispose();
        }

        if (result.Outcome != OperationOutcome.Succeeded && result.Outcome != OperationOutcome.DryRun)
        {
            RecordFailure(scope.OperationId, "restore", result.Messages.FirstOrDefault() ?? "还原失败");
            scope.Journal.Append(
                scope.OperationId,
                OperationStage.Failed,
                result.Outcome == OperationOutcome.Cancelled ? "cancelled" : "failed",
                result.Messages.FirstOrDefault() ?? "还原失败");
            var view = await SafeWriteReportAsync(
                scope.OperationId, "restore", started, result.Outcome,
                result.Messages.FirstOrDefault() ?? "还原失败", problems).ConfigureAwait(false);
            result.ReportPath = view?.MarkdownPath ?? result.ReportPath;
        }

        await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private SnapshotRef? ResolveSnapshotRef(string? snapshotId)
    {
        lock (_stateGate)
        {
            if (!string.IsNullOrWhiteSpace(snapshotId))
            {
                var found = new[] { _state.PreUpdateSnapshot, _state.LastSnapshot }
                    .FirstOrDefault(reference => reference is not null
                        && string.Equals(reference.Id, snapshotId, StringComparison.OrdinalIgnoreCase));
                if (found is not null) return found;

                var path = _paths.SnapshotDir(snapshotId!);
                if (Directory.Exists(path))
                {
                    return new SnapshotRef { Id = snapshotId!, Path = path };
                }

                return null;
            }

            return _state.PreUpdateSnapshot ?? _state.LastSnapshot;
        }
    }

    public Task<CheckResult?> GetLastCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(_lastCheck);

    // ---------------------------------------------------------------- reports

    public Task<ReportView?> GetLatestReportAsync(CancellationToken cancellationToken = default)
        => _reports.ReadLatestAsync(_paths, cancellationToken);

    public Task<ReportView?> GetReportAsync(string path, CancellationToken cancellationToken = default)
        => _reports.ReadAsync(path, cancellationToken);

    private async Task<ReportView?> WriteReportAsync(
        string operationId,
        string kind,
        DateTimeOffset started,
        OperationOutcome outcome,
        string summary,
        IEnumerable<ReportProblem>? problems = null,
        IEnumerable<ReportAction>? actions = null,
        IEnumerable<string>? warnings = null,
        SnapshotRef? snapshot = null,
        EvidenceLevel evidence = EvidenceLevel.None,
        JsonObject? extra = null,
        CancellationToken cancellationToken = default)
    {
        var report = new OperationReport
        {
            OperationId = operationId,
            Kind = kind,
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Outcome = outcome,
            Summary = summary,
            AppVersion = _installation?.AppVersion,
            DshVersion = _installation?.DshVersion,
            Snapshot = snapshot,
            Evidence = evidence,
            Problems = problems?.ToList() ?? new List<ReportProblem>(),
            Actions = actions?.ToList() ?? new List<ReportAction>(),
            Warnings = warnings?.ToList() ?? new List<string>(),
            Extra = extra,
        };

        try
        {
            var view = await _reports.WriteAsync(report, _paths, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (_state.LastOperation is { } last
                    && string.Equals(last.Id, operationId, StringComparison.Ordinal))
                {
                    last.ReportPath = view.MarkdownPath ?? view.JsonPath;
                    StateStore.Save(_paths, _state);
                }
            }

            return view;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Failed to write report", operationId);
            return null;
        }
    }

    private Task<ReportView?> SafeWriteReportAsync(
        string operationId,
        string kind,
        DateTimeOffset started,
        OperationOutcome outcome,
        string summary,
        IEnumerable<ReportProblem> problems)
        => WriteReportAsync(operationId, kind, started, outcome, summary, problems, null, null, null, EvidenceLevel.None, null, CancellationToken.None);

    private void RecordFailure(string operationId, string kind, string summary)
    {
        if (string.Equals(summary, "操作已取消", StringComparison.Ordinal)) return;

        lock (_stateGate)
        {
            _state.LastOperation = new LastOperation
            {
                Id = operationId,
                Kind = kind,
                Outcome = OperationOutcome.Failed,
                At = _clock.UtcNow,
                Summary = summary,
            };
            _state.Phase = UpdatePhase.Failed;
            StateStore.Save(_paths, _state);
        }

        AddActivity(KindLabel(kind) + "失败", summary, "error");
    }

    // ---------------------------------------------------------------- doctor / config

    public async Task<DoctorResult> RunDoctorAsync(CancellationToken cancellationToken = default)
    {
        var installation = await ResolveInstallationAsync(force: true, cancellationToken).ConfigureAwait(false);
        DoctorResult result;
        try
        {
            result = await _doctor.RunAsync(installation, _paths, _config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Doctor failed");
            result = new DoctorResult { Healthy = false, Summary = "体检失败：" + exception.Message };
        }

        result.Checks.Add(new DoctorCheck
        {
            Name = "data-root",
            Ok = Directory.Exists(_paths.DataRoot),
            Detail = _paths.DataRoot,
        });

        var unfinished = JournalWriter.HasUnfinishedOperation(_paths, out var unfinishedId);
        result.Checks.Add(new DoctorCheck
        {
            Name = "unfinished-journal",
            Ok = !unfinished,
            Detail = unfinished ? $"存在未完成操作 {unfinishedId}" : "无",
        });

        SnapshotRef? lastSnapshot;
        lock (_stateGate)
        {
            lastSnapshot = _state.LastSnapshot;
        }

        if (lastSnapshot is not null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "last-snapshot",
                Ok = _snapshots.Verify(lastSnapshot),
                Detail = lastSnapshot.Path,
            });
        }

        result.Checks.Add(new DoctorCheck
        {
            Name = "autostart",
            Ok = true,
            Detail = _autostart.IsEnabled() ? "已启用" : "未启用",
        });

        result.Healthy = result.Checks.All(check => check.Ok);
        if (string.IsNullOrEmpty(result.Summary))
        {
            result.Summary = result.Healthy ? "环境正常" : "存在问题，见明细";
        }

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task SaveConfigAsync(LoomConfig config, CancellationToken cancellationToken = default)
    {
        var autostartChanged = config.Autostart != _config.Autostart;
        ConfigStore.Normalize(config);

        // Apply the OS side effect first: if HKCU Run cannot be written, do not persist a config
        // that claims autostart is enabled.
        if (autostartChanged)
        {
            await ApplyAutostartAsync(config.Autostart, cancellationToken).ConfigureAwait(false);
        }

        ConfigStore.Save(_paths, config);
        _config = config;
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await ApplyAutostartAsync(enabled, cancellationToken).ConfigureAwait(false);
        _config.Autostart = enabled;
        ConfigStore.Save(_paths, _config);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAutostartAsync(bool enabled, CancellationToken cancellationToken)
    {
        var executable = System.Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            throw new LoomException(LoomErrorCodes.Unsupported, "无法确定当前程序路径。");
        }

        var command = $"\"{executable}\" --tray";
        await _autostart.SetEnabledAsync(enabled, command, cancellationToken).ConfigureAwait(false);
        var actual = _autostart.IsEnabled();
        if (actual != enabled)
        {
            throw new LoomException(
                LoomErrorCodes.Failed,
                enabled
                    ? "写入开机自启失败（HKCU Run 不可写），设置未保存。"
                    : "关闭开机自启失败（HKCU Run 不可写），设置未保存。");
        }
        _logger.Log(LogLevel.Info, $"Autostart {(enabled ? "enabled" : "disabled")}: {command}");
    }

    // ---------------------------------------------------------------- notifications

    private async Task RaiseNotificationAsync(
        string eventId,
        string title,
        string message,
        NotificationKind kind,
        string? reportPath,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (string.Equals(_state.LastNotifiedEventId, eventId, StringComparison.Ordinal)) return;
            _state.LastNotifiedEventId = eventId;
            _state.LastNotifiedAt = _clock.UtcNow;
            try
            {
                StateStore.Save(_paths, _state);
            }
            catch
            {
                // best effort
            }
        }

        lock (_notifiedGate)
        {
            if (!_notifiedEventIds.Add(eventId)) return;
            if (_notifiedEventIds.Count > 200)
            {
                _notifiedEventIds.Clear();
                _notifiedEventIds.Add(eventId);
            }
        }

        var notification = new LoomNotification
        {
            EventId = eventId,
            Title = title,
            Message = message,
            Kind = kind,
            ReportPath = reportPath,
            At = _clock.UtcNow,
        };

        try
        {
            NotificationRaised?.Invoke(this, notification);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Notification handler failed");
        }

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- operation scope

    private async Task<OperationScope> BeginOperationAsync(
        string kind,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? gateHandle = null;
        try
        {
            gateHandle = await _operationGate.AcquireAsync(kind, cancellationToken).ConfigureAwait(false);
            var operationId = LoomPaths.NewOperationId(kind);
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _busy = true;
            _busyLabel = BusyLabel(kind);
            var journal = new JournalWriter(_paths, operationId);
            journal.Append(operationId, OperationStage.Start, "start", kind);
            var scope = new OperationScope(this, operationId, kind, DateTimeOffset.Now, journal, gateHandle)
            {
                Progress = progress,
            };
            gateHandle = null;
            RaiseBusyStatus();
            return scope;
        }
        catch
        {
            gateHandle?.Dispose();
            _operationLock.Release();
            throw;
        }
    }

    private void EndOperation(OperationScope scope)
    {
        _busy = false;
        _busyLabel = null;
        try
        {
            _currentCts?.Dispose();
        }
        catch
        {
        }

        _currentCts = null;
        scope.GateHandle?.Dispose();
        _operationLock.Release();
        RaiseBusyStatus();
    }

    public void CancelCurrentOperation()
    {
        try
        {
            _currentCts?.Cancel();
        }
        catch
        {
        }
    }

    private void RaiseBusyStatus()
    {
        LoomStatus status;
        lock (_statusGate)
        {
            _status.Busy = _busy;
            _status.BusyLabel = _busyLabel;
            if (_busy)
            {
                _status.TrayState = TrayStatus.Busy;
                _status.Headline = _busyLabel ?? "正在处理";
                _status.Detail = "完成前请勿关闭 Loom。";
            }

            status = _status;
        }

        RaiseStatusChanged(status);
    }

    private static string BusyLabel(string kind) => kind switch
    {
        "snapshot" => "正在创建快照",
        "check" => "正在检查兼容性",
        "restore" => "正在还原文件",
        "doctor" => "正在体检",
        _ => "正在处理",
    };

    private static bool VersionsDiffer(string? before, string? after)
    {
        if (string.Equals(before ?? "", after ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (SemVersion.TryParse(before, out var left) && SemVersion.TryParse(after, out var right))
        {
            return left.CompareTo(right) != 0;
        }

        return true;
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly LoomService _owner;
        private bool _disposed;

        public OperationScope(
            LoomService owner,
            string operationId,
            string kind,
            DateTimeOffset startedAt,
            JournalWriter journal,
            IDisposable? gateHandle)
        {
            _owner = owner;
            OperationId = operationId;
            Kind = kind;
            StartedAt = startedAt;
            Journal = journal;
            GateHandle = gateHandle;
            Token = owner._currentCts?.Token ?? CancellationToken.None;
        }

        public string OperationId { get; }
        public string Kind { get; }
        public DateTimeOffset StartedAt { get; }
        public JournalWriter Journal { get; }
        public IDisposable? GateHandle { get; }
        public CancellationToken Token { get; }
        public IProgress<OperationProgress>? Progress { get; init; }

        public void Report(OperationStage stage, string message, int? percent = null)
            => Progress?.Report(new OperationProgress { Stage = stage, Message = message, Percent = percent });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.EndOperation(this);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
        }
        catch
        {
        }

        try
        {
            _logger.Flush();
        }
        catch
        {
        }

        _operationLock.Dispose();
    }
}
