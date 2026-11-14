using Loom.Services;
using System.Windows.Input;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.ViewModels;

/// <summary>View model for the first-run wizard (scope, located installation, autostart, first snapshot).</summary>
public sealed class FirstRunViewModel : ObservableObject
{
    private readonly ILoomService _loom;
    private string _installDirText = "正在定位…";
    private string _profileDirText = "—";
    private string _appVersionText = "—";
    private string _dshVersionText = "—";
    private bool _found;
    private bool _autostart = true;
    private bool _isBusy;
    private string _snapshotResultText = "";
    private bool _snapshotSucceeded;
    private bool _snapshotDone;
    private string _primaryText = UiStrings.FirstRunSnapshot;
    private string _errorText = "";

    public FirstRunViewModel(ILoomService loom)
    {
        _loom = loom;
        RelocateCommand = new AsyncRelayCommand(RelocateAsync, () => !IsBusy);
        TakeSnapshotCommand = new AsyncRelayCommand(TakeSnapshotAsync, () => !IsBusy);
        FinishCommand = new AsyncRelayCommand(FinishAsync, () => !IsBusy);
        LaterCommand = new RelayCommand(() => Later?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenProfileFolderCommand = new RelayCommand(OpenProfileFolder);
        Update();
        _ = LocateIfNeededAsync();
    }

    /// <summary>The tray refreshes asynchronously; make sure the wizard shows a located installation.</summary>
    private async Task LocateIfNeededAsync()
    {
        try
        {
            if (_loom.GetStatus().Installation is { Found: true }) return;
            IsBusy = true;
            await _loom.LocateAsync().ConfigureAwait(true);
            Update();
        }
        catch
        {
            // the wizard still offers "重新定位" on failure
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event EventHandler? Completed;
    public event EventHandler? Later;
    public event EventHandler<string>? SnapshotFinished;

    public ICommand RelocateCommand { get; }
    public ICommand TakeSnapshotCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand OpenProfileFolderCommand { get; }

    public string InstallDirText { get => _installDirText; private set => Set(ref _installDirText, value); }
    public string ProfileDirText { get => _profileDirText; private set => Set(ref _profileDirText, value); }
    public string AppVersionText { get => _appVersionText; private set => Set(ref _appVersionText, value); }
    public string DshVersionText { get => _dshVersionText; private set => Set(ref _dshVersionText, value); }
    public bool Found { get => _found; private set { Set(ref _found, value); OnPropertyChanged(nameof(NotFound)); OnPropertyChanged(nameof(LocationStatusText)); } }
    public bool NotFound => !Found;
    public string LocationStatusText => Found ? "已定位 DeepSeek Harness 桌面版" : "未找到 DeepSeek Harness 桌面版";
    public bool Autostart { get => _autostart; set => Set(ref _autostart, value); }
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); OnPropertyChanged(nameof(IsIdle)); RaiseCommands(); } }
    public bool IsIdle => !IsBusy;
    public string SnapshotResultText { get => _snapshotResultText; private set { Set(ref _snapshotResultText, value); OnPropertyChanged(nameof(HasSnapshotResult)); } }
    public bool HasSnapshotResult => SnapshotResultText.Length > 0;
    public bool SnapshotSucceeded { get => _snapshotSucceeded; private set => Set(ref _snapshotSucceeded, value); }
    public bool SnapshotDone { get => _snapshotDone; private set { Set(ref _snapshotDone, value); OnPropertyChanged(nameof(CanFinish)); } }
    public bool CanFinish => SnapshotDone || !IsBusy;
    public string PrimaryText { get => _primaryText; private set => Set(ref _primaryText, value); }
    public string ErrorText { get => _errorText; private set { Set(ref _errorText, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorText.Length > 0;

    public void Update() => ApplyStatus(_loom.GetStatus());

    /// <summary>Applies a status snapshot (also used by <c>ui render</c> previews with demo data).</summary>
    public void ApplyStatus(LoomStatus status)
    {
        var installation = status.Installation;
        Found = installation is not null && installation.Found;
        InstallDirText = string.IsNullOrWhiteSpace(installation?.InstallDir) ? "未找到安装目录" : installation!.InstallDir;
        ProfileDirText = string.IsNullOrWhiteSpace(installation?.ProfileDir) ? "未找到 desktop profile" : installation!.ProfileDir;
        AppVersionText = installation?.AppVersion ?? "unknown";
        DshVersionText = installation?.DshVersion ?? "unknown";
    }

    private async Task RelocateAsync()
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            var installation = await _loom.LocateAsync().ConfigureAwait(true);
            if (installation is null || !installation.Found)
            {
                Found = false;
                InstallDirText = "未找到安装目录";
                ProfileDirText = "未找到 desktop profile";
                ErrorText = "仍未找到安装。请确认已安装 DeepSeek Harness 桌面版。";
                return;
            }

            Found = true;
            InstallDirText = installation.InstallDir;
            ProfileDirText = installation.ProfileDir;
            AppVersionText = installation.AppVersion;
            DshVersionText = installation.DshVersion;
        }
        catch (Exception exception)
        {
            ErrorText = "定位失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TakeSnapshotAsync()
    {
        IsBusy = true;
        ErrorText = "";
        PrimaryText = "正在创建快照…";
        SnapshotResultText = "";
        try
        {
            var outcome = await _loom.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual })
                .ConfigureAwait(true);
            SnapshotDone = true;
            SnapshotSucceeded = outcome.Success;
            if (outcome.Success && outcome.Snapshot is not null)
            {
                SnapshotResultText = $"已创建首次快照：{outcome.Snapshot.PluginCount} 个插件 · {UiStrings.FormatBytes(outcome.Snapshot.SizeBytes)}";
            }
            else
            {
                SnapshotResultText = string.IsNullOrWhiteSpace(outcome.Message) ? "首次快照创建失败。" : outcome.Message;
            }

            SnapshotFinished?.Invoke(this, SnapshotResultText);
            Update();
        }
        catch (Exception exception)
        {
            SnapshotDone = true;
            SnapshotSucceeded = false;
            SnapshotResultText = "首次快照创建失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
            PrimaryText = SnapshotDone ? UiStrings.FirstRunFinish : UiStrings.FirstRunSnapshot;
        }
    }

    private async Task FinishAsync()
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            var config = CloneConfig(_loom.Config);
            config.FirstRunCompleted = true;
            config.Autostart = Autostart;
            config.Theme = ThemeManager.RequestedTheme;
            await _loom.SaveConfigAsync(config).ConfigureAwait(true);
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorText = "保存首次运行设置失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static LoomConfig CloneConfig(LoomConfig source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        InstallDir = source.InstallDir,
        DshHome = source.DshHome,
        ProfileDir = source.ProfileDir,
        DataRoot = source.DataRoot,
        AutoUpgradePolicy = source.AutoUpgradePolicy,
        AllowMajorUpgrade = source.AllowMajorUpgrade,
        KeepSnapshots = source.KeepSnapshots,
        MaxSnapshotBytes = source.MaxSnapshotBytes,
        MaxArchiveBytes = source.MaxArchiveBytes,
        FullSnapshotOnUpdate = source.FullSnapshotOnUpdate,
        AutoSnapshotOnStartup = source.AutoSnapshotOnStartup,
        AutoRestoreAfterRecovery = source.AutoRestoreAfterRecovery,
        Notifications = source.Notifications,
        QuietHoursStart = source.QuietHoursStart,
        QuietHoursEnd = source.QuietHoursEnd,
        QuietHoursEnabled = source.QuietHoursEnabled,
        Autostart = source.Autostart,
        LogLevel = source.LogLevel,
        Theme = source.Theme,
        FirstRunCompleted = source.FirstRunCompleted,
        HealthCheckSeconds = source.HealthCheckSeconds,
        StartupSnapshotDelaySeconds = source.StartupSnapshotDelaySeconds,
        ProfileChangeDebounceSeconds = source.ProfileChangeDebounceSeconds,
        OperationTimeoutSeconds = source.OperationTimeoutSeconds,
    };

    private void OpenProfileFolder()
    {
        var profile = _loom.GetStatus().Installation?.ProfileDir;
        if (!string.IsNullOrWhiteSpace(profile)) Services.ShellLauncher.OpenFolder(profile);
    }

    private void RaiseCommands()
    {
        (RelocateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TakeSnapshotCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (FinishCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LaterCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanFinish));
    }
}
