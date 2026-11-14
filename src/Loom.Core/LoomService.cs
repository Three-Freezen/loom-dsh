using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Restore;
using Loom.Core.Update;
using Loom.Core.Upgrade;

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
    private readonly UpgradeService _upgrades;
    private readonly PackageRestoreService _packageRestore;
    private readonly OfflineRollbackService _rollback;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _statusGate = new();
    private readonly object _stateGate = new();
    private readonly object _configGate = new();
    private readonly object _activityGate = new();
    private readonly List<ActivityItem> _activities = new();
    private readonly TickGate _tickGate;

    /// <summary>每个 Tick 最多自动处理的插件数量（串行执行；超过的留到下一个 Tick）。</summary>
    private const int MaxAutoUpgradesPerTick = 3;

    /// <summary>没有进程内检查结果时，自动升级前最多每 10 分钟补跑一次检查。</summary>
    private static readonly TimeSpan AutoUpgradeCheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>回滚点可用性探测（列目录 + 校验哈希）的节流间隔。</summary>
    private static readonly TimeSpan RollbackProbeInterval = TimeSpan.FromSeconds(30);

    private LoomConfig _config;
    private readonly LoomState _state;
    private LoomStatus _status = new();
    private DshInstallation? _installation;
    private DateTimeOffset _installationResolvedAt = DateTimeOffset.MinValue;
    private int _busy;
    private string? _busyLabel;
    private CancellationTokenSource? _currentCts;
    private DateTimeOffset? _appFirstSeenAt;
    private string? _checkedDshVersion;
    private CheckResult? _lastCheck;
    private DateTimeOffset _lastAutoUpgradeCheckAt = DateTimeOffset.MinValue;
    private string? _rollbackOpIdCache;
    private DateTimeOffset _rollbackOpIdCheckedAt = DateTimeOffset.MinValue;
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
        IOperationGate operationGate,
        UpgradeService upgrades,
        PackageRestoreService packageRestore,
        OfflineRollbackService rollback)
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
        _upgrades = upgrades ?? throw new ArgumentNullException(nameof(upgrades));
        _packageRestore = packageRestore ?? throw new ArgumentNullException(nameof(packageRestore));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _tickGate = new TickGate(_logger);
        Updates = new UpdateService(_paths, () => _config, config =>
        {
            ConfigStore.Normalize(config);
            ConfigStore.Save(_paths, config);
        }, _logger, MutateConfig);
        _paths.EnsureDirectories();
        _state.ToolVersion = LoomPaths.ToolVersion;
        _status = BuildStatus(null, false, null, new SnapshotStorageInfo());
    }

    public LoomPaths Paths => _paths;
    public LoomConfig Config => _config;
    public UpdateService Updates { get; }
    public UpgradeService Upgrades => _upgrades;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

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

        var busy = IsBusy;
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
            Busy = busy,
            BusyLabel = _busyLabel,
            RecentActivity = BuildActivity(lastOperation, lastCheck, lastSnapshot, preUpdate),
            // 二期可选字段（旧消费方忽略即可）：UI 的“回滚”按钮与包级恢复入口都据此启用。
            RollbackOperationId = ResolveRollbackOperationId(),
            PackageRestoreAvailable = _config.PackageRestoreEnabled && (lastSnapshot is not null || preUpdate is not null),
        };

        if (installation is null)
        {
            status.TrayState = TrayStatus.NotFound;
            status.Headline = "未找到 DSH 桌面版";
            status.Detail = "在设置中指定安装目录，或运行体检查看原因。";
            return status;
        }

        if (busy)
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
        "upgrade" => "插件升级",
        "recovery" => "包级恢复",
        "rollback" => "离线回滚",
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

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        // 托盘定时器每 15s 触发；长操作可能超时。TickGate 用状态机串行化，重叠的 Tick 直接跳过；
        // P3-R2：上一轮超过 StallThreshold 时由 TickGate 取消它并安全复位（等待退出或标记 abandoned），
        // 下一轮仍能继续。Tick 内部使用“应用退出 token + 本轮 token”的链接令牌。
        var operationId = LoomPaths.NewOperationId("tick");
        var tick = _tickGate.TryEnter(operationId);
        if (tick is null) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tick.Token);
        var token = linked.Token;
        try
        {
            if (tick.ResetOf is { } reset)
            {
                // P3-R2：复位本身必须可观测——ERROR 日志 + Failed 通知，eventId 带旧 Tick 的 opId。
                _logger.Log(
                    LogLevel.Error,
                    $"Tick 看门狗已复位：旧 Tick op={reset.OperationId} 运行 {TickGate.FormatDuration(reset.RunningFor)}"
                    + (reset.Abandoned ? "，等待退出超时，已标记 abandoned。" : "，已确认旧 Tick 退出。"));
                await RaiseNotificationAsync(
                    "tick-stalled-" + (string.IsNullOrEmpty(reset.OperationId) ? operationId : reset.OperationId),
                    "定时任务卡死已复位",
                    $"上一轮定时任务运行 {TickGate.FormatDuration(reset.RunningFor)} 未结束，已取消并安全复位"
                    + (reset.Abandoned ? "（旧任务标记为 abandoned）。" : "。")
                    + "自动快照/自动升级会在下一轮继续。",
                    NotificationKind.Failed,
                    null,
                    token).ConfigureAwait(false);
            }

            if (IsBusy) return;

            await RefreshStatusAsync(token).ConfigureAwait(false);
            var installation = _installation;
            if (installation is null || !installation.Found) return;

            var running = _status.AppRunning;
            if (!running)
            {
                _appFirstSeenAt = null;

                // 二期策略 A：只有 DSH 完全退出（profile 不再被写）时才允许自动升级插件。
                await MaybeAutoUpgradeAsync(installation, token).ConfigureAwait(false);
                return;
            }

            _appFirstSeenAt ??= _clock.UtcNow;

            await DetectPendingUpdateAsync(installation, token).ConfigureAwait(false);
            if (IsBusy) return;
            await DetectVersionChangeAsync(installation, token).ConfigureAwait(false);
            if (IsBusy) return;
            await MaybeAutoSnapshotAsync(installation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 看门狗取消（或应用退出）：复位路径已经记 ERROR/通知，这里保持安静。
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Tick failed");
        }
        finally
        {
            tick.Dispose();
        }
    }

    /// <summary>测试/诊断用：Tick 看门狗（阈值、复位等待、快照）。</summary>
    internal TickGate TickGate => _tickGate;

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

    // ---------------------------------------------------------------- 策略 A 自动升级

    /// <summary>
    /// 二期策略 A 自动升级：仅在开关打开、策略为 A、DSH 已完全退出、profile 空闲时，
    /// 对检查结果里 <see cref="CompatibilityLevel.Incompatible"/> 的插件串行执行升级
    /// （每个插件走 <see cref="UpgradePluginAsync"/>：查询候选 → 决策 → 备份 → 安装 → 校验）。
    /// 每次最多 <see cref="MaxAutoUpgradesPerTick"/> 个；失败即停止本 Tick，留到下一轮/人工处理。
    /// 安全边界：unverified/unknown 一律不自动装；没有兼容版本时只写报告与通知，绝不自动授予风险豁免；
    /// 绝不结束 DSH 进程、绝不写 DSH 安装目录或 app.asar。
    /// </summary>
    private async Task MaybeAutoUpgradeAsync(DshInstallation installation, CancellationToken cancellationToken)
    {
        if (!_config.AutoUpgradeEnabled) return;
        if (!string.Equals((_config.AutoUpgradePolicy ?? "").Trim(), "A", StringComparison.OrdinalIgnoreCase)) return;
        if (IsBusy) return;

        // 只用进程内的检查结果（“更新后自动检查”或用户手动 check 产生）。
        // 进程重启后没有插件级明细：最多每 10 分钟补跑一次检查，避免每个 Tick 都跑检查。
        var check = _lastCheck;
        if (check is null)
        {
            if (_clock.UtcNow - _lastAutoUpgradeCheckAt < AutoUpgradeCheckInterval) return;
            _lastAutoUpgradeCheckAt = _clock.UtcNow;
            var outcome = await RunCheckAsync(new CheckRequest { Auto = true }, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.Success || outcome.Result is null) return;
            check = outcome.Result;
        }

        var targets = CollectIncompatiblePlugins(check);
        if (targets.Count == 0) return;

        // DSH 必须完全退出 + profile 未被其它写入者占用：只读探测，绝不结束进程。
        if (IsAppRunning(installation)) return;
        if (IsProfileLocked(installation)) return;

        _logger.Log(
            LogLevel.Info,
            $"策略 A 自动升级开始：{targets.Count} 个不兼容插件（每个 Tick 最多处理 {MaxAutoUpgradesPerTick} 个）");

        foreach (var plugin in targets.Take(MaxAutoUpgradesPerTick))
        {
            if (IsBusy) return;
            cancellationToken.ThrowIfCancellationRequested();

            var result = await UpgradePluginAsync(
                new UpgradeRequest
                {
                    Installation = installation,
                    PackageName = plugin.Name,
                    InstalledVersion = plugin.Version,
                    TargetDshVersion = installation.DshVersion,
                    AllowPrerelease = _config.AllowPrereleaseUpgrade,
                },
                progress: null,
                cancellationToken).ConfigureAwait(false);

            if (ShouldStopAutoUpgrade(result))
            {
                _logger.Log(
                    LogLevel.Warn,
                    $"策略 A 自动升级停止本 Tick：{plugin.Name} 结果 {result.Outcome}（{result.Decision.BlockReason}）");
                return;
            }
        }
    }

    /// <summary>检查结果里“不兼容且版本已知”的插件（按包名去重，保持检查顺序）。</summary>
    private static List<PluginSnapshot> CollectIncompatiblePlugins(CheckResult? check)
    {
        if (check?.Plugins is null || check.Plugins.Count == 0) return new List<PluginSnapshot>();
        return check.Plugins
            .Where(plugin => plugin.Compatibility == CompatibilityLevel.Incompatible)
            .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Name) && !string.IsNullOrWhiteSpace(plugin.Version))
            .GroupBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>失败（含“无法写入”的待人工原因）即停止本 Tick；“无兼容候选/已最新”只是跳过。</summary>
    private static bool ShouldStopAutoUpgrade(UpgradeExecutionResult result) => result.Outcome switch
    {
        OperationOutcome.Failed or OperationOutcome.Cancelled => true,
        OperationOutcome.NeedsManual =>
            result.Decision.BlockReason is UpgradeBlockReason.AppRunning
                or UpgradeBlockReason.RegistryUnavailable
                or UpgradeBlockReason.InvalidInput,
        _ => false,
    };

    /// <summary>只读进程探测；异常按“仍在运行”处理（失败关闭，绝不结束进程）。</summary>
    private bool IsAppRunning(DshInstallation installation)
    {
        try
        {
            return _processes.FindDshProcesses(installation.InstallDir).Count > 0;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "自动升级前进程探测失败，按“仍在运行”处理");
            return true;
        }
    }

    /// <summary>profile 锁/插件管理器是否忙（只读检查；异常按“忙”处理）。</summary>
    private bool IsProfileLocked(DshInstallation installation)
    {
        try
        {
            return _locks.Inspect(installation).Busy;
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "自动升级前 profile 锁检查失败，按“忙”处理");
            return true;
        }
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

    // ---------------------------------------------------------------- 二期：插件升级 / 包级恢复

    /// <summary>
    /// 策略 A 单插件升级入口（CLI/托盘/自动 Tick 共用）：
    /// 查询候选 → 决策 → 写前备份 → <c>add &lt;pkg&gt;@&lt;ver&gt; --save-exact</c> → 回读校验 → 失败回滚。
    /// 安装目录/数据目录/配置/opId/超时由服务补齐；服务不结束 DSH 进程（App 未退出时返回 NeedsManual）。
    /// </summary>
    public Task<UpgradeExecutionResult> UpgradePluginAsync(
        UpgradeRequest request,
        CancellationToken cancellationToken = default) =>
        UpgradePluginAsync(request, progress: null, cancellationToken);

    /// <summary>同 <see cref="UpgradePluginAsync(UpgradeRequest, CancellationToken)"/>，额外汇报阶段进度。</summary>
    public async Task<UpgradeExecutionResult> UpgradePluginAsync(
        UpgradeRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = await BeginOperationAsync(
                "upgrade",
                progress,
                cancellationToken,
                requireCrossProcessGate: !request.DryRun)
            .ConfigureAwait(false);
        var started = scope.StartedAt;
        var journal = scope.Journal;
        var result = new UpgradeExecutionResult { OperationId = scope.OperationId, Outcome = OperationOutcome.Failed };
        var problems = new List<ReportProblem>();
        var actions = new List<ReportAction>();

        try
        {
            scope.Report(OperationStage.Locate, "定位 DSH 安装");
            if (request.Installation is null || string.IsNullOrWhiteSpace(request.Installation.ProfileDir))
            {
                var installation = await ResolveInstallationAsync(force: false, scope.Token).ConfigureAwait(false);
                if (installation is null || !installation.Found)
                {
                    throw new LoomException(LoomErrorCodes.NotFound, "未找到 DeepSeek Harness 桌面版安装目录，无法升级插件。");
                }

                request.Installation = installation;
            }

            // 服务侧统一补齐：数据目录、配置、opId、目标运行时、超时与 prerelease 开关都以当前配置为准，
            // 避免每个调用方各写一份（也保证 CLI 的 --allow-prerelease 与设置页开关合并生效）。
            request.Paths = _paths;
            request.Config = _config;
            request.OperationId = scope.OperationId;
            if (string.IsNullOrWhiteSpace(request.TargetDshVersion))
            {
                request.TargetDshVersion = request.Installation.DshVersion;
            }

            request.AllowPrerelease = request.AllowPrerelease || _config.AllowPrereleaseUpgrade;
            request.QueryTimeout = TimeSpan.FromSeconds(_config.UpgradeQueryTimeoutSeconds);
            request.InstallTimeout = TimeSpan.FromSeconds(_config.UpgradeInstallTimeoutSeconds);

            scope.Report(OperationStage.Plan, $"查询兼容候选：{request.PackageName}");
            result = await _upgrades.UpgradeAsync(request, scope.Token).ConfigureAwait(false);
            result.OperationId = scope.OperationId;
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
            _logger.LogException(exception, "Plugin upgrade failed", scope.OperationId);
        }
        catch (Exception exception)
        {
            result.Outcome = OperationOutcome.Failed;
            result.Messages.Add("插件升级失败：" + exception.Message);
            _logger.LogException(exception, "Plugin upgrade failed", scope.OperationId);
        }
        finally
        {
            scope.Dispose();
        }

        var summary = BuildUpgradeSummary(request, result);
        if (result.Outcome is OperationOutcome.Failed or OperationOutcome.NeedsManual)
        {
            var code = result.ErrorCode ?? result.Audit?.FailureClass.ToString() ?? LoomErrorCodes.Failed;
            foreach (var text in result.Messages)
            {
                problems.Add(new ReportProblem
                {
                    Severity = result.Outcome == OperationOutcome.Failed ? Severity.Error : Severity.Warning,
                    Code = code,
                    Message = text,
                    Subject = request.PackageName,
                });
            }
        }

        if (result.Audit is { } audit)
        {
            foreach (var rejection in audit.Rejected.Take(8))
            {
                problems.Add(new ReportProblem
                {
                    Severity = Severity.Info,
                    Code = rejection.Code,
                    Message = rejection.Reason,
                    Subject = rejection.Version,
                });
            }
        }

        if (result.Decision.ShouldUpgrade)
        {
            actions.Add(new ReportAction
            {
                Stage = "upgrade",
                Description = $"安装 {request.PackageName}@{result.Decision.TargetVersion}（--save-exact）",
                Target = request.PackageName,
                Applied = result.Outcome == OperationOutcome.Succeeded,
            });
            if (!string.IsNullOrWhiteSpace(result.CommandLine))
            {
                actions.Add(new ReportAction { Stage = "command", Description = result.CommandLine!, Target = request.PackageName });
            }
        }

        var extra = new JsonObject
        {
            ["packageName"] = request.PackageName,
            ["installedVersion"] = request.InstalledVersion,
            ["installedVersionAfter"] = result.Audit?.InstalledVersionAfter,
            ["targetVersion"] = result.Decision.TargetVersion,
            ["targetDshVersion"] = request.TargetDshVersion,
            ["decision"] = result.Decision.Kind.ToString(),
            ["blockReason"] = result.Decision.BlockReason.ToString(),
            ["failureClass"] = result.Audit?.FailureClass.ToString(),
            ["backupDir"] = result.BackupDir,
            ["auditPath"] = result.AuditPath,
            ["profileChanged"] = result.ProfileChanged,
            ["dryRun"] = request.DryRun,
        };

        var (title, message, notificationKind) = result.Outcome switch
        {
            OperationOutcome.Succeeded => (
                "插件已升级",
                $"已升级 {request.PackageName} 到 {result.Decision.TargetVersion}（备份与审计已写入数据目录）；重启 DSH 后生效。",
                NotificationKind.Info),
            OperationOutcome.DryRun => (
                "插件升级演练",
                summary,
                NotificationKind.Info),
            OperationOutcome.NeedsManual => (
                "插件升级需人工处理",
                summary,
                NotificationKind.NeedsManual),
            OperationOutcome.Cancelled => (
                "插件升级已取消",
                summary,
                NotificationKind.Info),
            OperationOutcome.Skipped => (
                "插件升级已跳过",
                summary,
                NotificationKind.Info),
            _ => (
                "插件升级失败",
                summary,
                NotificationKind.Failed),
        };

        await FinishOperationAsync(
            scope.OperationId,
            "upgrade",
            started,
            result.Outcome,
            summary,
            problems,
            actions,
            extra,
            title,
            message,
            notificationKind,
            journal,
            cancellationToken).ConfigureAwait(false);

        await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static string BuildUpgradeSummary(UpgradeRequest request, UpgradeExecutionResult result)
    {
        if (result.Messages.Count > 0 && !string.IsNullOrWhiteSpace(result.Messages[0])) return result.Messages[0];
        if (result.Outcome == OperationOutcome.Succeeded) return $"已升级 {request.PackageName} 到 {result.Decision.TargetVersion}。";
        if (!string.IsNullOrWhiteSpace(result.Decision.Reason)) return result.Decision.Reason;
        return "插件升级未完成（未改动 profile）。";
    }

    /// <summary>只生成包级恢复计划：读快照文本 + 当前 profile，**不写任何文件、不调用 CLI**。</summary>
    public async Task<PackageRestorePlan> PreviewPackageRestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installation = request.Installation is { ProfileDir.Length: > 0 }
            ? request.Installation
            : await RequireInstallationAsync(cancellationToken).ConfigureAwait(false);
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? LoomPaths.NewOperationId("recovery")
            : request.OperationId;
        return await BuildPackageRestorePlanAsync(installation, request, operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PackageRestorePlan> BuildPackageRestorePlanAsync(
        DshInstallation installation,
        PackageRestoreRequest request,
        string operationId,
        CancellationToken cancellationToken)
    {
        // 快照来源优先级：请求里显式给的 manifest → 请求里 plan 的 snapshotId → 状态里的更新前/最近快照。
        var hint = !string.IsNullOrWhiteSpace(request.Snapshot?.Id)
            ? request.Snapshot!.Id
            : !string.IsNullOrWhiteSpace(request.Plan?.SnapshotId)
                ? request.Plan.SnapshotId
                : null;

        var snapshotRef = ResolveSnapshotRef(hint);
        if (snapshotRef is null)
        {
            return BlockedRestorePlan(
                operationId,
                "没有可用的快照，无法生成包级恢复计划；请先创建快照。",
                PackageRestoreFailureKind.SnapshotInputMissing);
        }

        var manifest = await _snapshots.LoadAsync(snapshotRef, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return BlockedRestorePlan(
                operationId,
                $"快照 {snapshotRef.Id} 缺失或校验失败，无法生成包级恢复计划。",
                PackageRestoreFailureKind.SnapshotInputMissing);
        }

        var snapshotDir = string.IsNullOrWhiteSpace(snapshotRef.Path) ? _paths.SnapshotDir(snapshotRef.Id) : snapshotRef.Path;
        var snapshotProfileDir = Path.Combine(snapshotDir, "profile");
        var options = new PackageRestoreOptions
        {
            // 只读探测：DSH 仍在运行 → planner 把 DshFullyExited 判为不满足（绝不结束进程）。
            AppRunning = IsAppRunning(installation),
            ProfileLocked = IsProfileLocked(installation),
            // 未做 pnpm store 预检时保持乐观；真缺缓存由 CLI 输出分类兜底为 OfflineCacheMissing。
            OfflineCacheAvailable = true,
            // 联网回退必须“配置允许 + 本次用户同意”双重成立，默认不联网。
            AllowNetworkFallback = request.AllowNetworkFallback && _config.PackageRestoreAllowNetworkFallback,
            ReadyTimeoutSeconds = _config.PackageRestoreReadyTimeoutSeconds,
            InstallTimeoutSeconds = _config.PackageRestoreInstallTimeoutSeconds,
            Force = request.Force,
        };

        return PackageRestorePlanner.Create(new PackageRestorePlanInput
        {
            Snapshot = manifest,
            SnapshotDir = snapshotDir,
            SnapshotPackageJson = ReadTextOrNull(Path.Combine(snapshotProfileDir, DshDefaults.ManifestFile)),
            SnapshotLockfile = ReadTextOrNull(Path.Combine(snapshotProfileDir, DshDefaults.LockfileFile)),
            Current = _profileReader.Read(installation, strict: false),
            Installation = installation,
            Config = _config,
            Options = options,
            OperationId = operationId,
        });
    }

    private static PackageRestorePlan BlockedRestorePlan(
        string operationId,
        string message,
        PackageRestoreFailureKind kind) => new()
    {
        OperationId = operationId,
        IsBlocked = true,
        OfflineOnly = true,
        Summary = message,
        BlockingFailure = new PackageRestoreFailure
        {
            Kind = kind,
            Message = message,
            ManualAction = "先创建或校验快照（loom-dsh snapshot / loom-dsh doctor），再重试 package restore。",
        },
    };

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 包级恢复（备份 → 写 package.json/pnpm-lock.yaml → 离线 install → 校验；失败只回滚本 op）。
    /// 开关关闭时直接拒绝且不写盘；DSH 运行时只失败给人工入口，**绝不结束进程**。
    /// </summary>
    public Task<PackageRestoreResult> PackageRestoreAsync(
        PackageRestoreRequest request,
        CancellationToken cancellationToken = default) =>
        PackageRestoreAsync(request, progress: null, cancellationToken);

    /// <summary>同 <see cref="PackageRestoreAsync(PackageRestoreRequest, CancellationToken)"/>，额外汇报阶段进度。</summary>
    public async Task<PackageRestoreResult> PackageRestoreAsync(
        PackageRestoreRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_config.PackageRestoreEnabled)
        {
            // 开关关闭：不建 op、不写 journal/报告、不碰 profile，只返回可读原因（CLI 映射为失败）。
            var disabled = new PackageRestoreResult
            {
                OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                    ? LoomPaths.NewOperationId("recovery")
                    : request.OperationId,
                Outcome = OperationOutcome.Failed,
                Failure = new PackageRestoreFailure
                {
                    Kind = PackageRestoreFailureKind.Precondition,
                    Message = "包级恢复未启用：请在“设置 → 更新与兼容”里打开“包级恢复”，"
                              + "或把 config.json 的 packageRestoreEnabled 设为 true。",
                },
            };
            disabled.Messages.Add(disabled.Failure.Message);
            return disabled;
        }

        var scope = await BeginOperationAsync(
                "recovery",
                progress,
                cancellationToken,
                requireCrossProcessGate: !request.DryRun)
            .ConfigureAwait(false);
        var started = scope.StartedAt;
        var journal = scope.Journal;
        var result = new PackageRestoreResult { OperationId = scope.OperationId, Outcome = OperationOutcome.Failed };
        var problems = new List<ReportProblem>();
        var actions = new List<ReportAction>();

        try
        {
            scope.Report(OperationStage.Locate, "定位 DSH 安装");
            var installation = request.Installation is { ProfileDir.Length: > 0 }
                ? request.Installation
                : await RequireInstallationAsync(scope.Token).ConfigureAwait(false);

            var plan = request.Plan is { Steps.Count: > 0 } existing
                ? existing
                : await BuildPackageRestorePlanAsync(installation, request, scope.OperationId, scope.Token)
                    .ConfigureAwait(false);
            plan.OperationId = scope.OperationId;

            request.Installation = installation;
            request.Plan = plan;
            request.Paths = _paths;
            request.OperationId = scope.OperationId;
            request.InstallTimeoutSeconds = _config.PackageRestoreInstallTimeoutSeconds;
            // 运行状态只读探测：DSH 仍在运行时执行器直接失败并给人工入口（绝不结束进程）。
            request.AppRunning = !request.DryRun && plan.RequiresFullExit && IsAppRunning(installation);

            scope.Report(
                OperationStage.Apply,
                plan.IsBlocked ? "前置条件不满足，准备返回原因" : "执行包级恢复（备份 → 写文件 → 离线安装 → 校验）");
            result = await _packageRestore.RestoreAsync(request, scope.Token).ConfigureAwait(false);
            result.OperationId = scope.OperationId;
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
            result.Messages.Add(exception.Message);
            _logger.LogException(exception, "Package restore failed", scope.OperationId);
        }
        catch (Exception exception)
        {
            result.Outcome = OperationOutcome.Failed;
            result.Messages.Add("包级恢复失败：" + exception.Message);
            _logger.LogException(exception, "Package restore failed", scope.OperationId);
        }
        finally
        {
            scope.Dispose();
        }

        var summary = result.Messages.FirstOrDefault()
                      ?? result.Failure?.Message
                      ?? (result.Outcome == OperationOutcome.Succeeded
                          ? "包级恢复完成：依赖已按快照 lockfile 离线重建。"
                          : "包级恢复未完成。");

        if (result.Failure is { } failure)
        {
            problems.Add(new ReportProblem
            {
                Severity = result.Outcome == OperationOutcome.Failed ? Severity.Error : Severity.Warning,
                Code = failure.Kind.ToString(),
                Message = failure.Message,
                Detail = failure.Detail,
            });
        }
        else if (result.Outcome is OperationOutcome.Failed or OperationOutcome.NeedsManual)
        {
            foreach (var message in result.Messages)
            {
                problems.Add(new ReportProblem { Severity = Severity.Error, Code = LoomErrorCodes.Failed, Message = message });
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Failure?.ManualAction))
        {
            actions.Add(new ReportAction
            {
                Stage = "manual",
                Description = result.Failure!.ManualAction!,
                Target = "dsh.cmd",
            });
        }

        foreach (var step in result.Plan.OrderedSteps)
        {
            actions.Add(new ReportAction
            {
                Stage = step.Kind.ToString(),
                Description = step.Skip ? "跳过：" + (step.SkipReason ?? "无需执行") : step.Description,
                Target = step.TargetFile,
                Applied = step.Status == PackageRestoreStepStatus.Done,
            });
        }

        var extra = new JsonObject
        {
            ["snapshotId"] = result.Plan.SnapshotId,
            ["failureKind"] = result.Failure?.Kind.ToString(),
            ["backupDir"] = result.BackupDir,
            ["rollbackPoint"] = result.RollbackPointPath,
            ["verified"] = result.Verified,
            ["rolledBack"] = result.RolledBack,
            ["restartRequired"] = result.RequiresRestart,
            ["dryRun"] = request.DryRun,
        };

        var (title, notificationMessage, notificationKind) = result.Outcome switch
        {
            OperationOutcome.Succeeded => (
                "包级恢复完成",
                "依赖已按快照离线重建；请重启 DSH，Loom 会按首启判据检查结果。",
                NotificationKind.RestoreCompleted),
            OperationOutcome.DryRun => ("包级恢复演练", summary, NotificationKind.Info),
            OperationOutcome.NeedsManual => ("包级恢复需人工处理", summary, NotificationKind.NeedsManual),
            OperationOutcome.Cancelled => ("包级恢复已取消", summary, NotificationKind.Info),
            _ => ("包级恢复失败", summary, NotificationKind.Failed),
        };

        // 成功后按配置保留最近 N 份回滚点（绝不删当前 op 的备份目录）。
        if (result.Outcome == OperationOutcome.Succeeded) PruneRollbackBackups(scope.OperationId);

        await FinishOperationAsync(
            scope.OperationId,
            "recovery",
            started,
            result.Outcome,
            summary,
            problems,
            actions,
            extra,
            title,
            notificationMessage,
            notificationKind,
            journal,
            cancellationToken).ConfigureAwait(false);

        await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>该 opId 是否带回滚点且备份哈希校验通过（UI“回滚”按钮的启用依据）。</summary>
    public bool CanRollbackPackageOp(string opId)
    {
        if (string.IsNullOrWhiteSpace(opId)) return false;
        try
        {
            return _rollback.CanRollback(opId, out _);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "回滚点校验失败");
            return false;
        }
    }

    /// <summary>
    /// 按 opId 离线完整回滚（恢复文件 + <c>install --offline --frozen-lockfile</c> + 再次校验）。
    /// DSH 仍在运行时直接返回 NeedsManual，不写 profile、不联网、不结束进程。
    /// </summary>
    public async Task<OfflineRollbackResult> RollbackPackageOpAsync(
        string opId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(opId))
        {
            var invalid = new OfflineRollbackResult
            {
                OpId = opId ?? "",
                Outcome = OperationOutcome.Failed,
                Failure = new PackageRestoreFailure
                {
                    Kind = PackageRestoreFailureKind.Precondition,
                    Message = "未指定要回滚的操作 ID（用法：loom-dsh recovery rollback <opId>）。",
                },
            };
            invalid.Messages.Add(invalid.Failure.Message);
            return invalid;
        }

        var scope = await BeginOperationAsync("rollback", progress: null, cancellationToken).ConfigureAwait(false);
        var started = scope.StartedAt;
        var journal = scope.Journal;
        var result = new OfflineRollbackResult { OpId = scope.OperationId, Outcome = OperationOutcome.Failed };
        var problems = new List<ReportProblem>();
        var actions = new List<ReportAction>();
        string originalOpId = opId;

        try
        {
            scope.Report(OperationStage.Locate, "定位 DSH 安装");
            var installation = await RequireInstallationAsync(scope.Token).ConfigureAwait(false);

            if (!_rollback.CanRollback(opId, out var reason))
            {
                result = new OfflineRollbackResult
                {
                    OpId = originalOpId,
                    Outcome = OperationOutcome.NeedsManual,
                    Failure = new PackageRestoreFailure
                    {
                        Kind = PackageRestoreFailureKind.Precondition,
                        Message = "该操作没有可用的回滚点（备份缺失、损坏或哈希不一致）：" + (reason ?? "未知原因"),
                        ManualAction = "回滚点位于数据目录 restore-backup\\" + LoomPaths.SanitizeFileName(opId) + "\\，请人工核对。",
                    },
                };
                result.Messages.Add(result.Failure.Message);
            }
            else if (IsAppRunning(installation))
            {
                result = new OfflineRollbackResult
                {
                    OpId = originalOpId,
                    Outcome = OperationOutcome.NeedsManual,
                    Failure = new PackageRestoreFailure
                    {
                        Kind = PackageRestoreFailureKind.ProfileBusy,
                        Message = "DSH 仍在运行：离线回滚要求 App 完全退出，已停止且未写入任何文件（Loom 不会结束进程）。",
                        ManualAction = "请完全退出 DeepSeek Harness（含托盘/后台进程）后重试。",
                    },
                };
                result.Messages.Add(result.Failure.Message);
            }
            else
            {
                scope.Report(OperationStage.Rollback, $"离线回滚：{opId}");
                result = await _rollback.RollbackAsync(opId, installation, scope.Token).ConfigureAwait(false);
                result.OpId = originalOpId;
            }
        }
        catch (OperationCanceledException)
        {
            result = new OfflineRollbackResult { OpId = originalOpId, Outcome = OperationOutcome.Cancelled };
            result.Messages.Add("操作已取消");
        }
        catch (LoomException exception)
        {
            result = new OfflineRollbackResult
            {
                OpId = originalOpId,
                Outcome = exception.Code == LoomErrorCodes.NeedsManual
                    ? OperationOutcome.NeedsManual
                    : OperationOutcome.Failed,
                Failure = new PackageRestoreFailure
                {
                    Kind = PackageRestoreFailureKind.Precondition,
                    Message = exception.Message,
                },
            };
            result.Messages.Add(exception.Message);
            _logger.LogException(exception, "Package rollback failed", scope.OperationId);
        }
        catch (Exception exception)
        {
            result = new OfflineRollbackResult { OpId = originalOpId, Outcome = OperationOutcome.Failed };
            result.Messages.Add("离线回滚失败：" + exception.Message);
            _logger.LogException(exception, "Package rollback failed", scope.OperationId);
        }
        finally
        {
            scope.Dispose();
        }

        // 回滚后重新探测回滚点可用性（缓存立即失效）。
        _rollbackOpIdCheckedAt = DateTimeOffset.MinValue;

        var summary = result.Messages.FirstOrDefault()
                      ?? result.Failure?.Message
                      ?? (result.Outcome == OperationOutcome.Succeeded ? $"已回滚 {originalOpId}：文件与依赖已恢复到操作前状态。" : "离线回滚未完成。");

        if (result.Failure is { } failure)
        {
            problems.Add(new ReportProblem
            {
                Severity = result.Outcome == OperationOutcome.Succeeded ? Severity.Info : Severity.Warning,
                Code = failure.Kind.ToString(),
                Message = failure.Message,
                Detail = failure.Detail,
            });
        }

        if (result.RestoredFiles.Count > 0)
        {
            actions.Add(new ReportAction
            {
                Stage = "restore-files",
                Description = "恢复文件：" + string.Join("、", result.RestoredFiles),
                Target = "profile",
                Applied = result.Outcome == OperationOutcome.Succeeded,
            });
        }

        if (!string.IsNullOrWhiteSpace(result.Failure?.ManualAction))
        {
            actions.Add(new ReportAction
            {
                Stage = "manual",
                Description = result.Failure!.ManualAction!,
                Target = "dsh.cmd",
            });
        }

        var extra = new JsonObject
        {
            ["opId"] = originalOpId,
            ["scope"] = result.Point?.Scope.ToString(),
            ["failureKind"] = result.Failure?.Kind.ToString(),
            ["backupDir"] = result.BackupDir,
            ["installExitCode"] = result.InstallExitCode,
            ["verified"] = result.Verified,
        };

        var (title, notificationMessage, notificationKind) = result.Outcome switch
        {
            OperationOutcome.Succeeded => (
                "离线回滚完成",
                $"已回滚 {originalOpId}：文件与依赖恢复到操作前状态；重启 DSH 后生效。",
                NotificationKind.RestoreCompleted),
            OperationOutcome.NeedsManual => ("离线回滚需人工处理", summary, NotificationKind.NeedsManual),
            OperationOutcome.Cancelled => ("离线回滚已取消", summary, NotificationKind.Info),
            OperationOutcome.DryRun => ("离线回滚演练", summary, NotificationKind.Info),
            _ => ("离线回滚失败", summary, NotificationKind.Failed),
        };

        await FinishOperationAsync(
            scope.OperationId,
            "rollback",
            started,
            result.Outcome,
            summary,
            problems,
            actions,
            extra,
            title,
            notificationMessage,
            notificationKind,
            journal,
            cancellationToken).ConfigureAwait(false);

        await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>最近一个“可回滚”的包级 opId（带节流；UI 与状态字段共用）。</summary>
    private string? ResolveRollbackOperationId(bool force = false)
    {
        var now = _clock.UtcNow;
        if (!force && now - _rollbackOpIdCheckedAt < RollbackProbeInterval) return _rollbackOpIdCache;

        _rollbackOpIdCheckedAt = now;
        _rollbackOpIdCache = null;
        try
        {
            foreach (var opId in _rollback.ListRollbackPointIds())
            {
                if (_rollback.CanRollback(opId))
                {
                    _rollbackOpIdCache = opId;
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "枚举回滚点失败");
            _rollbackOpIdCache = null;
        }

        return _rollbackOpIdCache;
    }

    /// <summary>
    /// 按 <see cref="LoomConfig.KeepRollbackBackups"/> 清理最旧的回滚备份目录（best effort）。
    /// 只删 <c>restore-backup\&lt;opId&gt;\</c> 下的旧目录，绝不删当前 op，也绝不碰其它目录。
    /// </summary>
    private void PruneRollbackBackups(string currentOperationId)
    {
        var keep = Math.Max(1, _config.KeepRollbackBackups);
        try
        {
            var ids = _rollback.ListRollbackPointIds(); // 已按创建时间倒序
            if (ids.Count <= keep) return;

            var removed = 0;
            foreach (var opId in ids.Skip(keep))
            {
                if (string.Equals(opId, currentOperationId, StringComparison.OrdinalIgnoreCase)) continue;
                var directory = _paths.RestoreBackupDirFor(opId);
                var full = Path.GetFullPath(directory);
                // 只允许删除 restore-backup 目录的直接子目录（防御路径穿越/手改配置）。
                if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(_paths.RestoreBackupDir), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(full, recursive: true);
                    removed++;
                }
                catch (Exception exception)
                {
                    _logger.LogException(exception, "清理旧回滚备份失败（已跳过）：" + full);
                }
            }

            if (removed > 0)
            {
                _logger.Log(LogLevel.Info, $"已清理 {removed} 个旧回滚备份目录（保留最近 {keep} 份）。");
            }
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "回滚备份保留策略执行失败（已跳过）");
        }
    }

    /// <summary>
    /// 统一收尾：state.lastOperation → 活动流 → 报告 → journal → 通知（eventId 带 opId，天然去重）。
    /// 所有二期写操作（upgrade / recovery / rollback）共用，保证报告与通知格式一致。
    ///
    /// P3-R5 结论（有意保留当前顺序）：调用点都在 <c>scope.Dispose()</c>（释放跨进程门）之后。
    /// 门只保护会写 profile 的执行阶段；报告/state/journal 只写本进程数据根，且用同一个 operationId
    /// 与执行阶段绑定，逻辑上仍属同一次操作。若整体移入门内，会把跨进程门延长到报告落盘与
    /// <see cref="NotificationRaised"/> 订阅者回调（托盘可能刷新 UI/查询状态），既增加其它进程误
    /// Busy 的窗口，也引入“持门调用外部代码”的死锁风险；收益仅是理论上的 state 写并发窗口。
    /// 因此**不移动**，在此明确取舍。
    /// </summary>
    private async Task<string?> FinishOperationAsync(
        string operationId,
        string kind,
        DateTimeOffset started,
        OperationOutcome outcome,
        string summary,
        List<ReportProblem> problems,
        List<ReportAction> actions,
        JsonObject? extra,
        string title,
        string message,
        NotificationKind notificationKind,
        JournalWriter journal,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            _state.LastOperation = new LastOperation
            {
                Id = operationId,
                Kind = kind,
                Outcome = outcome,
                At = _clock.UtcNow,
                Summary = summary,
            };
            if (outcome == OperationOutcome.Failed) _state.Phase = UpdatePhase.Failed;
            StateStore.Save(_paths, _state);
        }

        AddActivity(
            KindLabel(kind),
            summary,
            outcome switch
            {
                OperationOutcome.Succeeded or OperationOutcome.DryRun => "ok",
                OperationOutcome.NeedsManual => "warn",
                OperationOutcome.Failed => "error",
                _ => "info",
            });

        var report = await WriteReportAsync(
            operationId,
            kind,
            started,
            outcome,
            summary,
            problems,
            actions,
            null,
            null,
            EvidenceLevel.None,
            extra,
            cancellationToken).ConfigureAwait(false);
        var reportPath = report?.MarkdownPath ?? report?.JsonPath;

        journal.Append(
            operationId,
            outcome == OperationOutcome.Succeeded ? OperationStage.Done : OperationStage.Failed,
            outcome.ToString().ToLowerInvariant(),
            summary);

        await RaiseNotificationAsync(
            $"{kind}-{operationId}",
            title,
            message,
            notificationKind,
            reportPath,
            cancellationToken).ConfigureAwait(false);

        return reportPath;
    }


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

        // P2-5：Tick 看门狗状态（不新增 UI，只在 doctor 里输出一条）。
        var tick = _tickGate.Snapshot();
        result.Checks.Add(new DoctorCheck
        {
            Name = "tick",
            Ok = !tick.Stalled,
            Detail = tick.IsRunning
                ? $"运行中 {FormatTickDuration(tick.RunningFor ?? TimeSpan.Zero)}"
                  + (tick.Stalled ? "（超过阈值，疑似卡死）" : "（正常）")
                : (tick.LastCompletedAt is { } completed
                    ? $"空闲（上次完成 {completed.ToLocalTime():yyyy-MM-dd HH:mm:ss}）"
                    : "空闲（尚未运行）")
                  + (tick.StalledResets > 0 && tick.LastResetAt is { } reset
                      ? $"；看门狗已复位 {tick.StalledResets} 次（最近 {reset.ToLocalTime():yyyy-MM-dd HH:mm:ss}）"
                      : ""),
        });

        result.Healthy = result.Checks.All(check => check.Ok);
        if (string.IsNullOrEmpty(result.Summary))
        {
            result.Summary = result.Healthy ? "环境正常" : "存在问题，见明细";
        }

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 配置的**单点后台修改入口**（P2-4）：锁内取内存最新副本、合并磁盘上更新的后台字段 →
    /// 只改调用方指定字段 → Normalize → 原子保存 → 更新内存 <see cref="_config"/>。
    /// 后台字段（如 <c>LastUpdateCheckUtc</c>）与设置页的整份保存并发时不会再互相覆盖。
    /// 以内存副本为基底是为了保留 <c>--install-dir</c> 等“只覆盖不落盘”的启动参数。
    /// </summary>
    public void MutateConfig(Action<LoomConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_configGate)
        {
            var config = CloneConfig(_config);
            PreserveBackgroundFields(config);
            ConfigStore.Normalize(config);
            mutate(config);
            ConfigStore.Normalize(config);
            ConfigStore.Save(_paths, config);
            _config = config;
        }
    }

    public async Task SaveConfigAsync(LoomConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var autostartChanged = config.Autostart != _config.Autostart;
        ConfigStore.Normalize(config);

        // Apply the OS side effect first: if HKCU Run cannot be written, do not persist a config
        // that claims autostart is enabled.
        if (autostartChanged)
        {
            await ApplyAutostartAsync(config.Autostart, cancellationToken).ConfigureAwait(false);
        }

        lock (_configGate)
        {
            // P2-4：整份保存在写盘前把“后台维护的字段”合并为最新值，防止设置页用旧快照
            // 把后台刚写入的 LastUpdateCheckUtc 覆盖回旧值。
            PreserveBackgroundFields(config);
            ConfigStore.Save(_paths, config);
            _config = config;
        }

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把后台维护的字段（目前是自动检查时间戳）合并为磁盘/内存中较新的值。</summary>
    private void PreserveBackgroundFields(LoomConfig incoming)
    {
        var latest = incoming.LastUpdateCheckUtc;
        var onDisk = TryLoadConfigFromDisk();
        if (onDisk is not null && onDisk.LastUpdateCheckUtc > latest) latest = onDisk.LastUpdateCheckUtc;
        if (_config.LastUpdateCheckUtc > latest) latest = _config.LastUpdateCheckUtc;
        incoming.LastUpdateCheckUtc = latest;
    }

    /// <summary>读取磁盘上的最新 config；文件不存在/损坏时返回 null（调用方退回内存副本）。</summary>
    private LoomConfig? TryLoadConfigFromDisk()
    {
        try
        {
            return File.Exists(_paths.ConfigFile) ? ConfigStore.Load(_paths) : null;
        }
        catch
        {
            return null;
        }
    }

    private static LoomConfig CloneConfig(LoomConfig source) =>
        JsonDefaults.Deserialize<LoomConfig>(JsonDefaults.Serialize(source)) ?? new LoomConfig();

    public async Task SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await ApplyAutostartAsync(enabled, cancellationToken).ConfigureAwait(false);
        lock (_configGate)
        {
            // 与 MutateConfig 共用一把锁；基底用内存副本（保留 --install-dir 等启动参数覆盖），
            // 只把后台维护的时间戳合并为磁盘/内存中的较新值。
            var config = CloneConfig(_config);
            PreserveBackgroundFields(config);
            config.Autostart = enabled;
            ConfigStore.Normalize(config);
            ConfigStore.Save(_paths, config);
            _config = config;
        }

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
            // 滚动窗口持久化最近 N 个 eventId：A/B/A 只通知一次，进程重启后也不重复。
            if (!NotificationLedger.TryRecord(_state, eventId, _clock.UtcNow)) return;
            try
            {
                StateStore.Save(_paths, _state);
            }
            catch
            {
                // best effort
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
        CancellationToken cancellationToken,
        bool requireCrossProcessGate = true)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? gateHandle = null;
        try
        {
            // P3-R8：dry-run 只读演练不碰 profile，不需要跨进程门（否则托盘 15 分钟安装会把只读
            // 演练挡在门外）；进程内串行化与 journal/report 仍照常。写操作必须 require=true。
            if (requireCrossProcessGate)
            {
                gateHandle = await _operationGate.AcquireAsync(kind, cancellationToken).ConfigureAwait(false);
            }
            var operationId = LoomPaths.NewOperationId(kind);
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _busyLabel = BusyLabel(kind);
            Volatile.Write(ref _busy, 1);
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
        Volatile.Write(ref _busy, 0);
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
        var busy = IsBusy;
        lock (_statusGate)
        {
            _status.Busy = busy;
            _status.BusyLabel = _busyLabel;
            if (busy)
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
        "upgrade" => "正在升级插件",
        "recovery" => "正在包级恢复",
        "rollback" => "正在离线回滚",
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

    /// <summary>doctor 的 tick 状态时长文本。</summary>
    private static string FormatTickDuration(TimeSpan value) =>
        value.TotalMinutes >= 1 ? $"{value.TotalMinutes:0.#} 分钟" : $"{value.TotalSeconds:0.#} 秒";

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
