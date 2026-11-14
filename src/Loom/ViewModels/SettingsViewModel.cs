using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Update;
using Loom.Services;

namespace Loom.ViewModels;

/// <summary>View model for the settings window (6 sections, edits <see cref="LoomConfig"/>).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly Regex TimePattern = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    private readonly ILoomService _loom;
    private LoomConfig _original = new();

    private string _theme = "system";
    private bool _autostart;
    private bool _autoSnapshotOnStartup;
    private bool _fullSnapshotOnUpdate;
    private bool _autoRestoreAfterRecovery;
    private bool _notifications;
    private bool _quietHoursEnabled;
    private string _quietHoursStart = "23:00";
    private string _quietHoursEnd = "08:00";
    private string _keepSnapshotsText = "5";
    private string _quotaGbText = "1";
    private string _autoUpgradePolicy = "A";

    // 二期：策略 A 自动升级 / 包级恢复（更新与兼容页）
    private bool _autoUpgradeEnabled;
    private bool _allowMajorUpgrade;
    private bool _allowPrereleaseUpgrade;
    private bool _packageRestoreEnabled;
    private bool _packageRestoreAllowNetworkFallback;
    private string _lastRollbackOpId = "";
    private bool _canRollbackPackage;
    private string _rollbackHintText = "暂无可回滚的包级操作";
    private string _rollbackStatusText = "";

    private string _dataRootText = "";
    private string _installDirText = "";
    private string _profileDirText = "";
    private string _appVersionText = "—";
    private string _dshVersionText = "—";
    private string _toolVersionText = LoomPaths.ToolVersion;
    private string _snapshotSummaryText = "0 份 · 0 B";
    private string _configPathText = "";
    private string _logsPathText = "";
    private string _errorText = "";
    private string _saveText = UiStrings.SettingsSave;
    private bool _isBusy;
    private string _savedHint = "";

    // 应用更新（GitHub Releases）
    private bool _autoCheckUpdate = true;
    private bool _deleteSetupAfterUpdate = true;
    private UpdateState _updateState = UpdateState.Idle;
    private bool _inAppUpdateSupported = true;
    private string _updateStatusText = "尚未检查更新";
    private string _updateVersionText = "—";
    private string _updateErrorText = "";
    private int _updateProgress;
    private bool _updateBusy;

    public SettingsViewModel(ILoomService loom)
    {
        _loom = loom;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
        OpenDataFolderCommand = new RelayCommand(() => ShellLauncher.OpenFolder(_loom.Paths.DataRoot));
        OpenBackupFolderCommand = new RelayCommand(() => ShellLauncher.OpenFolder(_loom.Paths.SnapshotsDir));
        OpenLogsFolderCommand = new RelayCommand(() => ShellLauncher.OpenFolder(_loom.Paths.LogsDir));
        OpenInstallFolderCommand = new RelayCommand(() =>
        {
            var dir = _loom.GetStatus().Installation?.InstallDir;
            if (!string.IsNullOrWhiteSpace(dir)) ShellLauncher.OpenFolder(dir);
        });
        RelocateCommand = new AsyncRelayCommand(RelocateAsync, () => !IsBusy);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !_updateBusy);
        DownloadUpdateCommand = new AsyncRelayCommand(
            DownloadUpdateAsync,
            () => !_updateBusy && _updateState == UpdateState.Available);
        ApplyUpdateCommand = new RelayCommand(
            ApplyUpdate,
            () => !_updateBusy && _updateState == UpdateState.Ready);
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
        RollbackPackageCommand = new AsyncRelayCommand(RollbackPackageAsync, () => CanRollbackPackage && !IsBusy);
        RefreshUpdateState();
    }

    public event EventHandler? Cancelled;
    public event EventHandler? Saved;
    public event EventHandler<string>? ThemePreviewRequested;

    /// <summary>Raised after the silent installer has been launched: the host must shut down so Inno can replace files.</summary>
    public event EventHandler? RestartRequested;

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand RelocateCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand OpenReleasePageCommand { get; }

    /// <summary>按 opId 离线回滚最近一次包级操作（仅 <see cref="CanRollbackPackage"/> 为真时可用）。</summary>
    public ICommand RollbackPackageCommand { get; }

    /// <summary>
    /// 回滚前的确认回调（由 View 注入 MessageBox，VM 不直接弹窗，便于单测与预览）。
    /// 未注入时按“用户未确认”处理：不执行任何写操作。
    /// </summary>
    public Func<string, bool>? ConfirmRollback { get; set; }

    /// <summary>保留份数下拉选项（字符串，便于直接与文本框/下拉绑定）。</summary>
    public IReadOnlyList<string> KeepOptions { get; } = new[] { "3", "5", "10", "20" };

    // ------------------------------------------------------------- editable
    public bool ThemeSystem
    {
        get => _theme == "system";
        set { if (value) SetTheme("system"); }
    }

    public bool ThemeDark
    {
        get => _theme == "dark";
        set { if (value) SetTheme("dark"); }
    }

    public bool ThemeLight
    {
        get => _theme == "light";
        set { if (value) SetTheme("light"); }
    }

    public bool Autostart { get => _autostart; set => Set(ref _autostart, value); }
    public bool AutoSnapshotOnStartup { get => _autoSnapshotOnStartup; set => Set(ref _autoSnapshotOnStartup, value); }
    public bool FullSnapshotOnUpdate { get => _fullSnapshotOnUpdate; set => Set(ref _fullSnapshotOnUpdate, value); }
    public bool AutoRestoreAfterRecovery { get => _autoRestoreAfterRecovery; set => Set(ref _autoRestoreAfterRecovery, value); }
    public bool Notifications { get => _notifications; set => Set(ref _notifications, value); }
    public bool QuietHoursEnabled { get => _quietHoursEnabled; set => Set(ref _quietHoursEnabled, value); }
    public string QuietHoursStart { get => _quietHoursStart; set => Set(ref _quietHoursStart, value); }
    public string QuietHoursEnd { get => _quietHoursEnd; set => Set(ref _quietHoursEnd, value); }
    public string KeepSnapshotsText { get => _keepSnapshotsText; set => Set(ref _keepSnapshotsText, value); }
    public string QuotaGbText { get => _quotaGbText; set => Set(ref _quotaGbText, value); }

    public bool PolicyAutoUpgrade
    {
        get => _autoUpgradePolicy == "A";
        set { if (value) SetPolicy("A"); }
    }

    public bool PolicyNotifyOnly
    {
        get => _autoUpgradePolicy == "B";
        set { if (value) SetPolicy("B"); }
    }

    public bool PolicyKeepOld
    {
        get => _autoUpgradePolicy == "C";
        set { if (value) SetPolicy("C"); }
    }

    // ------------------------------------------------------------- 二期：策略 A / 包级恢复

    /// <summary>策略 A 自动升级总开关（默认关闭；打开后 Tick 才会在 DSH 退出后自动升级兼容插件）。</summary>
    public bool AutoUpgradeEnabled
    {
        get => _autoUpgradeEnabled;
        set
        {
            if (!Set(ref _autoUpgradeEnabled, value)) return;
            OnPropertyChanged(nameof(AutoUpgradeHintText));
        }
    }

    /// <summary>允许 major（大版本）升级；默认关闭，跨大版本可能涉及插件数据格式迁移。</summary>
    public bool AllowMajorUpgrade { get => _allowMajorUpgrade; set => Set(ref _allowMajorUpgrade, value); }

    /// <summary>允许预发布（nightly/next）候选；仍稳定版优先，只有没有稳定兼容版本时才会选中。</summary>
    public bool AllowPrereleaseUpgrade { get => _allowPrereleaseUpgrade; set => Set(ref _allowPrereleaseUpgrade, value); }

    /// <summary>包级恢复总开关（默认关闭；打开后才允许 CLI / 托盘执行恢复与回滚）。</summary>
    public bool PackageRestoreEnabled
    {
        get => _packageRestoreEnabled;
        set
        {
            if (!Set(ref _packageRestoreEnabled, value)) return;
            OnPropertyChanged(nameof(PackageRestoreHintText));
        }
    }

    /// <summary>离线失败后是否允许“询问”联网重试（仍需逐次同意；默认关闭）。</summary>
    public bool PackageRestoreAllowNetworkFallback
    {
        get => _packageRestoreAllowNetworkFallback;
        set => Set(ref _packageRestoreAllowNetworkFallback, value);
    }

    /// <summary>最近一个可回滚的包级 opId（来自 <c>LoomStatus.RollbackOperationId</c>）。</summary>
    public string LastRollbackOpId { get => _lastRollbackOpId; private set => Set(ref _lastRollbackOpId, value); }

    /// <summary>“回滚”按钮的启用依据（只读属性 → XAML 必须显式 Mode=OneWay）。</summary>
    public bool CanRollbackPackage { get => _canRollbackPackage; private set => Set(ref _canRollbackPackage, value); }

    public string RollbackHintText { get => _rollbackHintText; private set => Set(ref _rollbackHintText, value); }
    public string RollbackStatusText { get => _rollbackStatusText; private set { Set(ref _rollbackStatusText, value); OnPropertyChanged(nameof(HasRollbackStatus)); } }
    public bool HasRollbackStatus => RollbackStatusText.Length > 0;

    public string AutoUpgradeHintText => AutoUpgradeEnabled
        ? "已开启：DSH 完全退出后，Loom 会自动把“不兼容且有兼容版本”的插件升级到兼容版本（每次最多 3 个）。"
        : "已关闭：只检查并提示，不会自动安装任何插件。";

    public string PackageRestoreHintText => PackageRestoreEnabled
        ? "已开启：包级恢复会写 profile 的两个核心文件并执行离线 install（需要 DSH 完全退出）。"
        : "已关闭：包级恢复与离线回滚都会被拒绝，不写 profile。";

    // ------------------------------------------------------------- update
    public bool AutoCheckUpdate { get => _autoCheckUpdate; set => Set(ref _autoCheckUpdate, value); }
    public bool DeleteSetupAfterUpdate { get => _deleteSetupAfterUpdate; set => Set(ref _deleteSetupAfterUpdate, value); }

    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }
    public string UpdateVersionText { get => _updateVersionText; private set => Set(ref _updateVersionText, value); }
    public string UpdateErrorText { get => _updateErrorText; private set { Set(ref _updateErrorText, value); OnPropertyChanged(nameof(HasUpdateError)); } }
    public bool HasUpdateError => UpdateErrorText.Length > 0;
    public int UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public bool InAppUpdateSupported { get => _inAppUpdateSupported; private set => Set(ref _inAppUpdateSupported, value); }
    public bool IsUpdateDownloading => _updateState == UpdateState.Downloading;
    public bool ShowDownloadUpdate => _inAppUpdateSupported && _updateState == UpdateState.Available;
    public bool ShowApplyUpdate => _updateState == UpdateState.Ready;
    public bool ShowUpdateProgress => _updateState == UpdateState.Downloading;
    public string UpdateActionText => _updateState == UpdateState.Checking ? "正在检查…" : "检查更新";

    // ------------------------------------------------------------- read-only
    public string DataRootText { get => _dataRootText; private set => Set(ref _dataRootText, value); }
    public string InstallDirText { get => _installDirText; private set => Set(ref _installDirText, value); }
    public string ProfileDirText { get => _profileDirText; private set => Set(ref _profileDirText, value); }
    public string AppVersionText { get => _appVersionText; private set => Set(ref _appVersionText, value); }
    public string DshVersionText { get => _dshVersionText; private set => Set(ref _dshVersionText, value); }
    public string ToolVersionText { get => _toolVersionText; private set => Set(ref _toolVersionText, value); }
    public string SnapshotSummaryText { get => _snapshotSummaryText; private set => Set(ref _snapshotSummaryText, value); }
    public string ConfigPathText { get => _configPathText; private set => Set(ref _configPathText, value); }
    public string LogsPathText { get => _logsPathText; private set => Set(ref _logsPathText, value); }

    public string ErrorText { get => _errorText; private set { Set(ref _errorText, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorText.Length > 0;
    public string SaveText { get => _saveText; private set => Set(ref _saveText, value); }
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public string SavedHint { get => _savedHint; private set { Set(ref _savedHint, value); OnPropertyChanged(nameof(HasSavedHint)); } }
    public bool HasSavedHint => SavedHint.Length > 0;

    public string ThemeSummary => UiStrings.ThemeLabel(_theme);

    public string FrequencySummary => _loom.Config.AutoSnapshotOnStartup ? "启动后自动快照" : "仅手动快照";

    public void Load(LoomConfig config, LoomStatus status)
    {
        _original = config;
        _theme = ThemeManager.Normalize(config.Theme);
        Autostart = config.Autostart;
        AutoSnapshotOnStartup = config.AutoSnapshotOnStartup;
        FullSnapshotOnUpdate = config.FullSnapshotOnUpdate;
        AutoRestoreAfterRecovery = config.AutoRestoreAfterRecovery;
        Notifications = config.Notifications;
        QuietHoursEnabled = config.QuietHoursEnabled;
        QuietHoursStart = config.QuietHoursStart;
        QuietHoursEnd = config.QuietHoursEnd;
        KeepSnapshotsText = config.KeepSnapshots.ToString(CultureInfo.InvariantCulture);
        QuotaGbText = (config.MaxSnapshotBytes / (1024d * 1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture);
        _autoUpgradePolicy = string.IsNullOrWhiteSpace(config.AutoUpgradePolicy) ? "A" : config.AutoUpgradePolicy;
        AutoCheckUpdate = config.AutoCheckUpdate;
        DeleteSetupAfterUpdate = config.DeleteSetupAfterUpdate;

        // 二期字段（旧 config.json 缺字段时由 LoomConfig 默认值兜底）
        AutoUpgradeEnabled = config.AutoUpgradeEnabled;
        AllowMajorUpgrade = config.AllowMajorUpgrade;
        AllowPrereleaseUpgrade = config.AllowPrereleaseUpgrade;
        PackageRestoreEnabled = config.PackageRestoreEnabled;
        PackageRestoreAllowNetworkFallback = config.PackageRestoreAllowNetworkFallback;
        RollbackStatusText = "";
        RefreshRollbackState(status);

        var installation = status.Installation;
        InstallDirText = string.IsNullOrWhiteSpace(installation?.InstallDir) ? "未找到（可在 CLI 用 --install-dir 指定）" : installation!.InstallDir;
        ProfileDirText = string.IsNullOrWhiteSpace(installation?.ProfileDir) ? "未找到" : installation!.ProfileDir;
        AppVersionText = installation?.AppVersion ?? "unknown";
        DshVersionText = installation?.DshVersion ?? "unknown";
        DataRootText = _loom.Paths.DataRoot;
        ConfigPathText = _loom.Paths.ConfigFile;
        LogsPathText = _loom.Paths.LogsDir;
        ToolVersionText = LoomPaths.ToolVersion;
        SnapshotSummaryText = $"{status.SnapshotCount} 份 · {UiStrings.FormatBytes(status.SnapshotBytes)}";

        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeDark));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(PolicyAutoUpgrade));
        OnPropertyChanged(nameof(PolicyNotifyOnly));
        OnPropertyChanged(nameof(PolicyKeepOld));
        OnPropertyChanged(nameof(ThemeSummary));
        ErrorText = "";
        SavedHint = "";
        RefreshUpdateState();
    }

    // ------------------------------------------------------------- update flow

    private void RefreshUpdateState()
    {
        var updates = _loom.Updates;
        _updateState = updates.State;
        InAppUpdateSupported = updates.InAppUpdateSupported;
        UpdateVersionText = string.IsNullOrWhiteSpace(updates.NewVersion) ? "—" : updates.NewVersion!;
        UpdateProgress = updates.ProgressPercent;
        UpdateErrorText = _updateState == UpdateState.Failed ? updates.ErrorText ?? "" : "";
        UpdateStatusText = _updateState switch
        {
            UpdateState.Idle => _inAppUpdateSupported
                ? "尚未检查更新"
                : "当前为免安装/开发版：可检查版本，但需手动下载安装包。",
            UpdateState.Checking => "正在检查更新…",
            UpdateState.UpToDate => updates.MessageText ?? "已是最新版本。",
            UpdateState.Available => $"发现新版本 {updates.NewVersion}，可下载安装。",
            UpdateState.Downloading => $"正在下载安装包… {updates.ProgressPercent}%",
            UpdateState.Ready => (_inAppUpdateSupported
                ? "安装包已下载，点击“重启并更新”静默安装。"
                : "安装包已下载；当前为免安装版，请手动运行下载的安装包。")
                + "（" + (updates.IntegrityVerified, updates.IntegrityText) switch
                {
                    (true, _) => "SHA256 已校验",
                    (false, { Length: > 0 } note) => note,
                    _ => "未校验",
                } + "）",
            UpdateState.Failed => "更新失败：" + (updates.ErrorText ?? "未知错误"),
            _ => "当前为免安装/开发版，不支持应用内更新。",
        };

        OnPropertyChanged(nameof(IsUpdateDownloading));
        OnPropertyChanged(nameof(ShowDownloadUpdate));
        OnPropertyChanged(nameof(ShowApplyUpdate));
        OnPropertyChanged(nameof(ShowUpdateProgress));
        OnPropertyChanged(nameof(UpdateActionText));
        (CheckUpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadUpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task CheckUpdateAsync()
    {
        _updateBusy = true;
        RefreshUpdateState();
        try
        {
            await _loom.Updates.CheckAsync().ConfigureAwait(true);
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateState();
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (!_loom.Updates.InAppUpdateSupported)
        {
            ErrorText = "当前为免安装/开发版，请到 GitHub Releases 手动下载安装包。";
            return;
        }

        _updateBusy = true;
        _loom.Updates.StateChanged += OnUpdateStateChanged;
        try
        {
            await _loom.Updates.DownloadAsync().ConfigureAwait(true);
        }
        finally
        {
            _loom.Updates.StateChanged -= OnUpdateStateChanged;
            _updateBusy = false;
            RefreshUpdateState();
        }
    }

    private void OnUpdateStateChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) RefreshUpdateState();
        else dispatcher.BeginInvoke(new Action(RefreshUpdateState));
    }

    private void ApplyUpdate()
    {
        try
        {
            _loom.Updates.ApplyAndRestart();
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorText = "启动安装程序失败：" + exception.Message;
        }
    }

    private void OpenReleasePage() => ShellLauncher.OpenUrl(_loom.Updates.ReleaseUrl);

    private void SetTheme(string theme)
    {
        if (_theme == theme) return;
        _theme = theme;
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeDark));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeSummary));
        ThemePreviewRequested?.Invoke(this, theme);
    }

    private void SetPolicy(string policy)
    {
        if (_autoUpgradePolicy == policy) return;
        _autoUpgradePolicy = policy;
        OnPropertyChanged(nameof(PolicyAutoUpgrade));
        OnPropertyChanged(nameof(PolicyNotifyOnly));
        OnPropertyChanged(nameof(PolicyKeepOld));
    }

    private async Task RelocateAsync()
    {
        IsBusy = true;
        try
        {
            var installation = await _loom.LocateAsync().ConfigureAwait(true);
            if (installation is null || !installation.Found)
            {
                ErrorText = "重新定位失败：未找到 DeepSeek Harness。";
                return;
            }

            InstallDirText = installation.InstallDir;
            ProfileDirText = installation.ProfileDir;
            AppVersionText = installation.AppVersion;
            DshVersionText = installation.DshVersion;
            ErrorText = "";
        }
        catch (Exception exception)
        {
            ErrorText = "重新定位失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------- 包级恢复 / 回滚

    /// <summary>用当前状态刷新“回滚”可用性（opId 来自 <see cref="LoomStatus.RollbackOperationId"/>）。</summary>
    private void RefreshRollbackState(LoomStatus status)
    {
        LastRollbackOpId = status.RollbackOperationId ?? "";
        var canRollback = !string.IsNullOrWhiteSpace(LastRollbackOpId)
                          && _loom.CanRollbackPackageOp(LastRollbackOpId);
        CanRollbackPackage = canRollback;
        RollbackHintText = canRollback
            ? $"最近可回滚的包级操作：{LastRollbackOpId}"
            : "暂无可回滚的包级操作（执行过包级恢复后才会出现回滚点）";
        OnPropertyChanged(nameof(CanRollbackPackage));
        (RollbackPackageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>确认后按 opId 离线完整回滚（文件 + 离线 install + 再次校验）。</summary>
    private async Task RollbackPackageAsync()
    {
        var opId = LastRollbackOpId;
        if (string.IsNullOrWhiteSpace(opId))
        {
            ErrorText = "没有可回滚的包级操作。";
            return;
        }

        // 二次确认由 View 注入（VM 不直接弹窗）；未注入时按“未确认”处理，绝不写 profile。
        var confirmed = ConfirmRollback?.Invoke(
            "将按操作 ID 离线回滚：\n"
            + "• " + opId + "\n"
            + "• 只恢复该操作的 profile 文件，并执行 install --offline --frozen-lockfile\n"
            + "• 需要 DSH 完全退出；Loom 不会结束任何进程、不会联网\n\n是否继续？") ?? false;
        if (!confirmed) return;

        IsBusy = true;
        ErrorText = "";
        RollbackStatusText = "正在离线回滚…";
        try
        {
            var result = await _loom.RollbackPackageOpAsync(opId).ConfigureAwait(true);
            RollbackStatusText = result.Outcome switch
            {
                OperationOutcome.Succeeded =>
                    $"已回滚 {opId}（校验{(result.Verified ? "通过" : "未通过")}），请重启 DSH。",
                OperationOutcome.NeedsManual => "回滚需要人工处理：" + (result.Failure?.Message ?? "见报告"),
                OperationOutcome.Cancelled => "回滚已取消。",
                _ => "回滚失败：" + (result.Failure?.Message ?? "见报告"),
            };
            if (result.Outcome != OperationOutcome.Succeeded
                && !string.IsNullOrWhiteSpace(result.Failure?.ManualAction))
            {
                RollbackStatusText += "（人工入口：" + result.Failure!.ManualAction + "）";
            }

            RefreshRollbackState(_loom.GetStatus());
        }
        catch (Exception exception)
        {
            ErrorText = "回滚失败：" + exception.Message;
            RollbackStatusText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把界面当前值合并成新的 <see cref="LoomConfig"/>（不落盘、不弹窗、不碰 WPF 资源），
    /// 供 <see cref="SaveAsync"/> 与单测共用：新增配置字段必须在这里显式透传，
    /// 否则保存设置会把它们重置为默认值（丢配置）。
    /// </summary>
    internal LoomConfig BuildUpdatedConfig(int keepSnapshots, double quotaGb)
    {
        return new LoomConfig
        {
            SchemaVersion = _original.SchemaVersion,
            InstallDir = _original.InstallDir,
            DshHome = _original.DshHome,
            ProfileDir = _original.ProfileDir,
            DataRoot = _original.DataRoot,
            AutoUpgradePolicy = _autoUpgradePolicy,
            AllowMajorUpgrade = AllowMajorUpgrade,
            // 二期新增字段必须全部透传（见类文档与 SettingsView 的“更新与兼容”页）。
            AutoUpgradeEnabled = AutoUpgradeEnabled,
            AllowPrereleaseUpgrade = AllowPrereleaseUpgrade,
            UpgradeQueryTimeoutSeconds = _original.UpgradeQueryTimeoutSeconds,
            UpgradeInstallTimeoutSeconds = _original.UpgradeInstallTimeoutSeconds,
            RequireAppExitForUpgrade = _original.RequireAppExitForUpgrade,
            PackageRestoreEnabled = PackageRestoreEnabled,
            PackageRestoreAllowNetworkFallback = PackageRestoreAllowNetworkFallback,
            PackageRestoreInstallTimeoutSeconds = _original.PackageRestoreInstallTimeoutSeconds,
            PackageRestoreReadyTimeoutSeconds = _original.PackageRestoreReadyTimeoutSeconds,
            KeepRollbackBackups = _original.KeepRollbackBackups,
            KeepSnapshots = keepSnapshots,
            MaxSnapshotBytes = (long)(quotaGb * 1024 * 1024 * 1024),
            MaxArchiveBytes = _original.MaxArchiveBytes,
            FullSnapshotOnUpdate = FullSnapshotOnUpdate,
            AutoSnapshotOnStartup = AutoSnapshotOnStartup,
            AutoRestoreAfterRecovery = AutoRestoreAfterRecovery,
            Notifications = Notifications,
            QuietHoursStart = QuietHoursStart.Trim(),
            QuietHoursEnd = QuietHoursEnd.Trim(),
            QuietHoursEnabled = QuietHoursEnabled,
            Autostart = Autostart,
            LogLevel = _original.LogLevel,
            Theme = _theme,
            AutoCheckUpdate = AutoCheckUpdate,
            DeleteSetupAfterUpdate = DeleteSetupAfterUpdate,
            LastUpdateCheckUtc = _original.LastUpdateCheckUtc,
            FirstRunCompleted = _original.FirstRunCompleted,
            HealthCheckSeconds = _original.HealthCheckSeconds,
            StartupSnapshotDelaySeconds = _original.StartupSnapshotDelaySeconds,
            ProfileChangeDebounceSeconds = _original.ProfileChangeDebounceSeconds,
            OperationTimeoutSeconds = _original.OperationTimeoutSeconds,
        };
    }

    private async Task SaveAsync()
    {
        SaveText = UiStrings.SettingsSaving;
        IsBusy = true;
        ErrorText = "";
        SavedHint = "";
        try
        {
            if (!int.TryParse(KeepSnapshotsText.Trim(), out var keep) || keep < 1 || keep > 50)
            {
                ErrorText = "保留份数需为 1–50 的整数。";
                return;
            }

            if (!double.TryParse(QuotaGbText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var quotaGb)
                || quotaGb < 0.1 || quotaGb > 100)
            {
                ErrorText = "快照配额需为 0.1–100 GB 之间的数字。";
                return;
            }

            if (!TimePattern.IsMatch(QuietHoursStart.Trim()) || !TimePattern.IsMatch(QuietHoursEnd.Trim()))
            {
                ErrorText = "静默时段请使用 HH:mm 格式（例如 23:00）。";
                return;
            }

            var updated = BuildUpdatedConfig(keep, quotaGb);

            await _loom.SaveConfigAsync(updated).ConfigureAwait(true);
            var autostartChanged = updated.Autostart != _original.Autostart;
            if (autostartChanged)
            {
                await _loom.SetAutostartAsync(updated.Autostart).ConfigureAwait(true);
            }

            _original = updated;
            ThemeManager.Apply(updated.Theme);
            SnapshotSummaryText = $"{_loom.GetStatus().SnapshotCount} 份 · {UiStrings.FormatBytes(_loom.GetStatus().SnapshotBytes)}";
            SavedHint = UiStrings.SettingsSaved;
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorText = "保存失败：" + exception.Message;
        }
        finally
        {
            SaveText = UiStrings.SettingsSave;
            IsBusy = false;
        }
    }

    /// <summary>Reverts the live theme preview after the user cancels.</summary>
    public void RevertThemePreview() => ThemeManager.Apply(_original.Theme);
}
