using System.Collections.ObjectModel;
using System.Windows.Input;
using Loom.Core.Abstractions;
using Loom.Core.Models;
using Loom.Services;

namespace Loom.ViewModels;

public sealed class ActivityRow
{
    public string Glyph { get; init; } = "\uE946";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string Kind { get; init; } = "info";
}

/// <summary>View model for the tray flyout (status card + primary actions + recent activity).</summary>
public sealed class TrayPopupViewModel : ObservableObject
{
    private readonly ILoomService _loom;
    private LoomStatus _status = new();
    private bool _localBusy;
    private string _statusLabel = UiStrings.StatusNotFound;
    private string _statusKey = "NotFound";
    private string _headline = "";
    private string _detail = "";
    private string _versionText = "—";
    private string _protectedText = "尚无快照";
    private string _snapshotText = "尚无快照记录";
    private string _usageText = "备份 0 B";
    private string _frequencyText = "自动检查已开启";
    private string _footerText = "";
    private string _feedbackText = "";
    private bool _feedbackIsError;
    private string _primaryText = UiStrings.PopupSnapshot;
    private bool _showIssuePanel;
    private string _issueTitle = "";
    private string _issueDetail = "";
    private string _retryText = UiStrings.PopupRetry;
    private bool _showRollback;
    private string _themeGlyph = "\uE706";
    private bool _showFirstRunHint;

    public TrayPopupViewModel(ILoomService loom)
    {
        _loom = loom;
        SnapshotCommand = new AsyncRelayCommand(TakeSnapshotAsync, () => !IsBusy);
        CheckCommand = new AsyncRelayCommand(RunCheckAsync, () => !IsBusy);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !IsBusy);
        BackupFolderCommand = new RelayCommand(OpenBackupFolder, () => !IsBusy);
        ReportCommand = new RelayCommand(() => ReportRequested?.Invoke(this, EventArgs.Empty));
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        RollbackCommand = new RelayCommand(
            () => RollbackRequested?.Invoke(this, EventArgs.Empty),
            () => !IsBusy);
        PluginManagerCommand = new RelayCommand(OpenPluginManager);
        FirstRunCommand = new RelayCommand(() => FirstRunRequested?.Invoke(this, EventArgs.Empty));
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
    }

    public event EventHandler? ReportRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? RollbackRequested;
    public event EventHandler? FirstRunRequested;

    public ICommand SnapshotCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand BackupFolderCommand { get; }
    public ICommand ReportCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand PluginManagerCommand { get; }
    public ICommand FirstRunCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    public ObservableCollection<ActivityRow> Activity { get; } = new();

    public string StatusLabel { get => _statusLabel; private set => Set(ref _statusLabel, value); }
    public string StatusKey { get => _statusKey; private set => Set(ref _statusKey, value); }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }
    public string ProtectedText { get => _protectedText; private set => Set(ref _protectedText, value); }
    public string SnapshotText { get => _snapshotText; private set => Set(ref _snapshotText, value); }
    public string UsageText { get => _usageText; private set => Set(ref _usageText, value); }
    public string FrequencyText { get => _frequencyText; private set => Set(ref _frequencyText, value); }
    public string FooterText { get => _footerText; private set => Set(ref _footerText, value); }
    public string FeedbackText { get => _feedbackText; private set => Set(ref _feedbackText, value); }
    public bool FeedbackIsError { get => _feedbackIsError; private set => Set(ref _feedbackIsError, value); }
    public bool HasFeedback => FeedbackText.Length > 0;
    public string PrimaryText { get => _primaryText; private set => Set(ref _primaryText, value); }
    public bool ShowIssuePanel { get => _showIssuePanel; private set => Set(ref _showIssuePanel, value); }
    public string IssueTitle { get => _issueTitle; private set => Set(ref _issueTitle, value); }
    public string IssueDetail { get => _issueDetail; private set => Set(ref _issueDetail, value); }
    public string RetryText { get => _retryText; private set => Set(ref _retryText, value); }
    public bool ShowRollback { get => _showRollback; private set => Set(ref _showRollback, value); }
    public string ThemeGlyph { get => _themeGlyph; private set => Set(ref _themeGlyph, value); }
    public bool ShowFirstRunHint { get => _showFirstRunHint; private set => Set(ref _showFirstRunHint, value); }
    public bool HasActivity => Activity.Count > 0;

    public bool IsBusy => _localBusy || _status.Busy;

    public string BusyText => _status.Busy ? _status.BusyLabel ?? UiStrings.StatusBusy : UiStrings.StatusBusy;

    public void Update(LoomStatus status)
    {
        _status = status;
        StatusKey = UiStrings.TrayStatusKey(status.TrayState);
        StatusLabel = UiStrings.TrayStatusLabel(status.TrayState);
        Headline = string.IsNullOrWhiteSpace(status.Headline) ? UiStrings.StatusNormal : status.Headline;
        Detail = status.Detail ?? "";
        VersionText = status.AppVersion ?? status.Installation?.AppVersion ?? "—";
        ProtectedText = status.ProtectedPlugins > 0 ? $"{status.ProtectedPlugins} 个插件已保护" : "尚无保护快照";
        SnapshotText = status.LastSnapshotAt is null
            ? "还没有快照，建议立即创建"
            : $"上次快照 {UiStrings.FormatDate(status.LastSnapshotAt)} · {TriggerLabel(status.LastSnapshotTrigger)}";
        UsageText = $"备份 {UiStrings.FormatBytes(status.SnapshotBytes)} · {status.SnapshotCount} 份";
        FrequencyText = _loom.Config.AutoSnapshotOnStartup ? "启动后自动快照已开启" : "启动后自动快照已关闭";
        FooterText = $"LOOM {Loom.Core.Infrastructure.LoomPaths.ToolVersion} · {UiStrings.PopupFooterRunning}";
        ThemeGlyph = ThemeManager.IsDark ? "\uE706" : "\uE708";
        ShowFirstRunHint = status.FirstRun;

        var failed = status.TrayState is TrayStatus.Failed;
        var needsManual = status.TrayState is TrayStatus.NeedsManual or TrayStatus.AwaitingExit or TrayStatus.NotFound;
        ShowIssuePanel = failed || needsManual;
        IssueTitle = status.Headline;
        IssueDetail = status.Detail ?? "";
        RetryText = status.TrayState == TrayStatus.AwaitingExit ? "我已退出，重试" : UiStrings.PopupRetry;
        ShowRollback = failed;

        Activity.Clear();
        foreach (var item in status.RecentActivity.Take(4))
        {
            Activity.Add(new ActivityRow
            {
                Glyph = UiStrings.ActivityGlyph(item.Kind),
                Title = item.Title,
                Detail = item.Detail ?? "",
                TimeText = UiStrings.FormatRelative(item.At),
                Kind = item.Kind,
            });
        }

        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(BusyText));
        RaiseCommandStates();
    }

    /// <summary>Refresh from the service; used when the flyout is opened.</summary>
    public void RefreshFromService()
    {
        _localBusy = false;
        Update(_loom.GetStatus());
        PrimaryText = UiStrings.PopupSnapshot;
    }

    public void SetLocalBusy(bool busy, string? label = null)
    {
        _localBusy = busy;
        PrimaryText = busy ? label ?? UiStrings.PopupSnapshotBusy : UiStrings.PopupSnapshot;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(BusyText));
        RaiseCommandStates();
    }

    public void SetFeedback(string? text, bool isError = false)
    {
        FeedbackText = text ?? "";
        FeedbackIsError = isError;
        OnPropertyChanged(nameof(HasFeedback));
    }

    private async Task TakeSnapshotAsync()
    {
        SetLocalBusy(true, UiStrings.PopupSnapshotBusy);
        SetFeedback(null);
        try
        {
            var outcome = await _loom.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual })
                .ConfigureAwait(true);
            if (outcome.Success)
            {
                var snapshot = outcome.Snapshot;
                SetFeedback(snapshot is null
                    ? outcome.Message
                    : $"快照完成 · {snapshot.PluginCount} 个插件 · {UiStrings.FormatBytes(snapshot.SizeBytes)}");
            }
            else
            {
                SetFeedback(string.IsNullOrWhiteSpace(outcome.Message) ? "快照失败" : outcome.Message, isError: true);
            }
        }
        catch (Exception exception)
        {
            SetFeedback("快照失败：" + exception.Message, isError: true);
        }
        finally
        {
            SetLocalBusy(false);
            Update(_loom.GetStatus());
        }
    }

    private async Task RunCheckAsync()
    {
        SetLocalBusy(true, UiStrings.PopupCheckBusy);
        SetFeedback(null);
        try
        {
            var outcome = await _loom.RunCheckAsync(new CheckRequest()).ConfigureAwait(true);
            if (!outcome.Success)
            {
                SetFeedback(string.IsNullOrWhiteSpace(outcome.Message) ? "检查失败" : outcome.Message, isError: true);
            }
            else
            {
                var result = outcome.Result;
                var text = result is null ? outcome.Message : result.Summary;
                if (result is not null && result.Issues.Count == 0) text = "兼容性检查通过";
                SetFeedback(text, result is not null && result.Issues.Count > 0);
            }
        }
        catch (Exception exception)
        {
            SetFeedback("检查失败：" + exception.Message, isError: true);
        }
        finally
        {
            SetLocalBusy(false);
            Update(_loom.GetStatus());
        }
    }

    private async Task RetryAsync()
    {
        await RunCheckAsync().ConfigureAwait(true);
    }

    private void OpenBackupFolder() => ShellLauncher.OpenFolder(_loom.Paths.SnapshotsDir);

    private void OpenPluginManager()
    {
        var profile = _loom.GetStatus().Installation?.ProfileDir;
        if (string.IsNullOrWhiteSpace(profile)) return;
        var pluginManager = System.IO.Path.Combine(profile, Loom.Core.Infrastructure.DshDefaults.PluginManagerDir);
        ShellLauncher.OpenFolder(System.IO.Directory.Exists(pluginManager) ? pluginManager : profile);
    }

    private void ToggleTheme()
    {
        var next = ThemeManager.IsDark ? "light" : "dark";
        ThemeManager.Apply(next);
        ThemeGlyph = ThemeManager.IsDark ? "\uE706" : "\uE708";
    }

    private void RaiseCommandStates()
    {
        (SnapshotCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (BackupFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RollbackCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string TriggerLabel(SnapshotTrigger? trigger) => trigger switch
    {
        SnapshotTrigger.Auto => "自动快照",
        SnapshotTrigger.PreUpdate => "更新前快照",
        SnapshotTrigger.PostUpdate => "更新后快照",
        SnapshotTrigger.Manual => "手动快照",
        _ => "快照",
    };

    /// <summary>
    /// Representative status used only by <c>ui render</c>: real installation info when available,
    /// plus demo snapshot/activity numbers so previews exercise the normal, populated layout.
    /// </summary>
    public static LoomStatus CreatePreviewStatus(DshInstallationInfo? realInstallation = null)
    {
        var installation = realInstallation is { Found: true }
            ? realInstallation
            : new DshInstallationInfo
            {
                InstallDir = @"D:\DeepSeek",
                ProfileDir = @"C:\Users\%USERNAME%\.dsh\profiles\desktop",
                DshHome = @"C:\Users\%USERNAME%\.dsh",
                AppVersion = "0.2.2-rc.1",
                DshVersion = "0.3.0",
                Found = true,
            };

        var now = DateTimeOffset.Now;
        return new LoomStatus
        {
            TrayState = TrayStatus.Normal,
            Phase = UpdatePhase.Idle,
            Headline = "已保护 3 个插件",
            Detail = "最近快照（应用 " + installation.AppVersion + " / 运行时 " + installation.DshVersion + "）。",
            Installation = installation,
            AppVersion = installation.AppVersion,
            DshVersion = installation.DshVersion,
            LastSnapshotAt = now.AddMinutes(-34),
            LastSnapshotTrigger = SnapshotTrigger.Auto,
            ProtectedPlugins = 3,
            SnapshotCount = 4,
            SnapshotBytes = 44_700_000,
            AppRunning = true,
            RecentActivity = new List<ActivityItem>
            {
                new() { At = now.AddMinutes(-34), Title = "快照完成 · 3 个插件", Detail = "自动快照", Kind = "ok" },
                new() { At = now.AddMinutes(-34), Title = "兼容检查通过", Detail = "无兼容性问题", Kind = "ok" },
                new() { At = now.AddMinutes(-48), Title = "自动升级 dsh-plugin-market", Detail = "1.73.19 → 1.74.2", Kind = "info" },
            },
        };
    }
}
