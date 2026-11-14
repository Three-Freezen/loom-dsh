using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Loom.Core.Abstractions;
using Loom.Core.Models;
using Loom.Core.Update;
using Loom.Services;
using Loom.ViewModels;
using Loom.Views;

// WPF/WinForms 同时启用时消除类型歧义
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace Loom;

/// <summary>
/// Notification-area host: WinForms <see cref="System.Windows.Forms.NotifyIcon"/> + status overlay icon,
/// WPF-drawn context menu (see <see cref="TrayMenuWindow"/>), tray flyout, settings/wizard/report
/// windows and balloon notifications. Everything that touches WPF windows runs on the WPF dispatcher.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly NotificationPresenter _notifications = new();

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _currentIcon;
    private IntPtr _iconHandle = IntPtr.Zero;
    private TrayStatus? _iconState;
    private bool _iconDark;
    private bool _started;
    private bool _disposed;

    private TrayMenuViewModel? _menuViewModel;
    private TrayMenuWindow? _menuWindow;
    private DateTime _menuLastClosedUtc = DateTime.MinValue;
    private TrayPopupViewModel? _popupViewModel;
    private TrayPopupWindow? _popupWindow;
    private SettingsViewModel? _settingsViewModel;
    private SettingsWindow? _settingsWindow;
    private FirstRunViewModel? _firstRunViewModel;
    private FirstRunWindow? _firstRunWindow;
    private ReportViewModel? _reportViewModel;
    private ReportWindow? _reportWindow;
    private LoomStatus _lastStatus = new();

    public TrayHost(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    private ILoomService Loom => _services.Loom;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        ThemeManager.Apply(Loom.Config.Theme);
        _iconDark = ThemeManager.IsDark;
        ThemeManager.ThemeChanged += OnThemeChanged;

        CreateTrayIcon();
        Subscribe();
        UpdateStatus(Loom.GetStatus());
        _ = RefreshStatusAsync();

        // 应用更新：先消费上次静默升级留下的安装包清理标记，再做启动后台检查（24h 节流）。
        Loom.Updates.ConsumePendingSetupCleanup();
        _ = AutoCheckUpdatesAsync();
    }

    private void CreateTrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Visible = true,
            Text = "Loom for DSH",
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            // 左键 → 状态弹层；右键 → WPF 自绘菜单（不再使用 WinForms ContextMenuStrip）
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowPopup();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowTrayMenu();
        };
        _notifyIcon.DoubleClick += (_, _) => ShowPopup();
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var last = _notifications.Last;
            if (last?.Kind == NotificationKind.UpdateDetected)
            {
                ShowSettings();
            }
            else if (last?.ReportPath is { Length: > 0 })
            {
                ShowReport(last.ReportPath);
            }
            else
            {
                ShowReport();
            }
        };

        ReplaceIcon(Loom.GetStatus().TrayState);
    }

    private void Subscribe()
    {
        Loom.StatusChanged += OnStatusChanged;
        Loom.NotificationRaised += OnNotificationRaised;
    }

    private void OnStatusChanged(object? sender, LoomStatus status) => OnUi(() => UpdateStatus(status));

    private void OnNotificationRaised(object? sender, LoomNotification notification) => Notify(notification);

    private async Task RefreshStatusAsync()
    {
        try
        {
            await Loom.RefreshStatusAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Tray status refresh failed");
        }
    }

    // ------------------------------------------------------------------ status → tray

    private void UpdateStatus(LoomStatus status)
    {
        _lastStatus = status;
        if (_notifyIcon is null) return;

        ReplaceIcon(status.TrayState);

        var headline = string.IsNullOrWhiteSpace(status.Headline) ? "Loom for DSH" : status.Headline;
        try
        {
            _notifyIcon.Text = UiStrings.Truncate(headline, 63);
        }
        catch
        {
            _notifyIcon.Text = "Loom for DSH";
        }

        _menuViewModel?.SetBusy(status.Busy);
        _popupViewModel?.Update(status);
    }

    private void ReplaceIcon(TrayStatus state, bool force = false)
    {
        if (_notifyIcon is null) return;
        if (!force && _iconState == state && _currentIcon is not null) return;

        try
        {
            // 托盘图标底色跟随应用主题（system 时即系统主题）：与软件内 LogoMark / 窗口图标同源同配色
            var (icon, handle) = TrayIconFactory.Create(state, _iconDark);
            var previousIcon = _currentIcon;
            var previousHandle = _iconHandle;
            _currentIcon = icon;
            _iconHandle = handle;
            _iconState = state;
            _notifyIcon.Icon = icon;

            previousIcon?.Dispose();
            TrayIconFactory.Destroy(previousHandle);
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Tray icon update failed");
        }
    }

    private void OnThemeChanged(object? sender, string theme) => OnUi(() =>
    {
        if (_disposed) return;

        var dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        if (_iconDark != dark)
        {
            _iconDark = dark;
            ReplaceIcon(_lastStatus.TrayState, force: true);
        }

        // 已打开窗口的标题栏/任务栏图标同步切换
        ApplyAppIcon(_settingsWindow);
        ApplyAppIcon(_firstRunWindow);
        ApplyAppIcon(_reportWindow);
    });

    private static void ApplyAppIcon(Window? window)
    {
        if (window is null) return;
        try
        {
            var icon = AppIcon.Current;
            if (icon is not null) window.Icon = icon;
        }
        catch
        {
            // best effort: a broken icon must not break window creation
        }
    }

    // ------------------------------------------------------------------ notifications

    /// <summary>Shows a balloon notification (respecting config, quiet hours and event de-duplication).</summary>
    public void Notify(LoomNotification notification)
    {
        if (notification is null) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => Notify(notification)));
            return;
        }

        if (_notifyIcon is null) return;
        try
        {
            _notifications.Present(_notifyIcon, notification, Loom.Config);
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Balloon notification failed");
        }
    }

    // ------------------------------------------------------------------ commands

    private async Task RunSnapshotAsync()
    {
        try
        {
            await Loom.TakeSnapshotAsync(new SnapshotRequest { Trigger = SnapshotTrigger.Manual }).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Tray snapshot failed");
            Notify(new LoomNotification
            {
                EventId = "ui-snapshot-failed-" + Guid.NewGuid().ToString("N"),
                Title = "快照失败",
                Message = exception.Message,
                Kind = NotificationKind.Failed,
                At = DateTimeOffset.Now,
            });
        }
    }

    private async Task RunCheckAsync()
    {
        try
        {
            await Loom.RunCheckAsync(new CheckRequest()).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Tray check failed");
        }
    }

    // ------------------------------------------------------------------ app updates

    /// <summary>Startup background check (throttled to once per 24 h, opt-out in settings).</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        try
        {
            var result = await Loom.Updates.AutoCheckIfDueAsync().ConfigureAwait(true);
            if (result is not null)
            {
                Notify(new LoomNotification
                {
                    EventId = "app-update-available-" + result.Version,
                    Title = "发现 Loom 新版本 " + result.Version,
                    Message = "打开设置页可下载并静默安装（安装版支持一键重启更新）。",
                    Kind = NotificationKind.UpdateDetected,
                    At = DateTimeOffset.Now,
                });
            }
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Auto update check failed");
        }
    }

    /// <summary>Tray menu "检查应用更新": reports the result and opens settings when an update exists.</summary>
    private async Task CheckUpdatesAsync()
    {
        var updates = Loom.Updates;
        try
        {
            await updates.CheckAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Tray update check failed");
        }

        if (updates.State == UpdateState.Failed)
        {
            Notify(new LoomNotification
            {
                EventId = "app-update-failed-" + Guid.NewGuid().ToString("N"),
                Title = "检查更新失败",
                Message = updates.ErrorText ?? "未知错误",
                Kind = NotificationKind.Failed,
                At = DateTimeOffset.Now,
            });
            return;
        }

        if (updates.State == UpdateState.Available)
        {
            Notify(new LoomNotification
            {
                EventId = "app-update-available-" + updates.NewVersion,
                Title = "发现 Loom 新版本 " + updates.NewVersion,
                Message = "设置页可下载并静默安装。",
                Kind = NotificationKind.UpdateDetected,
                At = DateTimeOffset.Now,
            });
            ShowSettings();
            return;
        }

        Notify(new LoomNotification
        {
            EventId = "app-update-latest-" + DateTimeOffset.Now.ToString("yyyyMMddHH"),
            Title = "已是最新版本",
            Message = string.IsNullOrWhiteSpace(updates.MessageText)
                ? "Loom 已是最新版本（" + updates.CurrentVersion + "）。"
                : updates.MessageText!,
            Kind = NotificationKind.Info,
            At = DateTimeOffset.Now,
        });
    }

    // ------------------------------------------------------------------ tray context menu

    public void ShowTrayMenu() => OnUi(ShowTrayMenuCore);

    /// <summary>
    /// Event-order guard: clicking the tray icon while the menu is open may deactivate/close the menu
    /// before the right-click handler runs. Without this, the same click would immediately reopen it;
    /// treat a focus-loss close within 250 ms as "the user toggled it off".
    /// </summary>
    private static readonly TimeSpan MenuToggleSuppression = TimeSpan.FromMilliseconds(250);

    private void ShowTrayMenuCore()
    {
        if (_disposed) return;
        if (_notifyIcon is null) return;

        // 再次右键 = 收起（与系统菜单一致的开关行为）
        if (_menuWindow is not null)
        {
            _menuWindow.Close();
            return;
        }

        if (DateTime.UtcNow - _menuLastClosedUtc < MenuToggleSuppression)
        {
            _menuLastClosedUtc = DateTime.MinValue;
            return;
        }

        try
        {
            var viewModel = new TrayMenuViewModel(new TrayMenuActions(
                OpenStatusPanel: OpenPopupFromMenu,
                Snapshot: () => _ = RunSnapshotAsync(),
                Check: () => _ = RunCheckAsync(),
                CheckUpdate: () => _ = CheckUpdatesAsync(),
                OpenReport: () => ShowReport(_lastStatus.LastReportPath),
                OpenSettings: ShowSettings,
                OpenBackupFolder: () => ShellLauncher.OpenFolder(_services.Paths.SnapshotsDir),
                Exit: _services.RequestExit));
            viewModel.SetBusy(_lastStatus.Busy);

            var window = new TrayMenuWindow(viewModel, MenuDiagnostics);
            window.Closed += (_, _) =>
            {
                if (!window.WasInvoked) _menuLastClosedUtc = DateTime.UtcNow;
                _menuWindow = null;
                _menuViewModel = null;
            };

            _menuViewModel = viewModel;
            _menuWindow = window;
            PositionMenu(window);
        }
        catch (Exception exception)
        {
            _menuWindow = null;
            _menuViewModel = null;
            _services.TryLogException(exception, "Tray context menu failed");
        }
    }

    /// <summary>
    /// Diagnostic sink for <see cref="TrayMenuWindow"/>: always writes to the debugger; WARN entries
    /// (foreground lock refused / capture fallback) are also persisted to the Loom log for
    /// real-machine troubleshooting.
    /// </summary>
    private void MenuDiagnostics(string message)
    {
        Debug.WriteLine("[TrayMenu] " + message);
        if (!message.StartsWith("WARN", StringComparison.Ordinal)) return;

        try
        {
            _services.TryLogException(new InvalidOperationException(message), "Tray menu activation diagnostic");
        }
        catch
        {
            // diagnostics must never break the tray
        }
    }

    /// <summary>
    /// Menu-triggered "打开状态面板": always opens the flyout (never toggles). The menu window is
    /// already fully closed when this runs — <see cref="TrayMenuWindow"/> queues the action from
    /// its Closed handler on the dispatcher.
    /// </summary>
    private void OpenPopupFromMenu() => OnUi(() => ShowPopupCore(forceOpen: true));

    // ------------------------------------------------------------------ windows

    public void ShowPopup() => OnUi(() => ShowPopupCore(forceOpen: false));

    private void ShowPopupCore(bool forceOpen)
    {
        if (_disposed) return;

        if (_popupWindow is not null)
        {
            if (!forceOpen)
            {
                _popupWindow.ForceClose();
                return;
            }

            var existing = _popupWindow;
            _popupWindow = null;
            try
            {
                existing.ForceClose();
            }
            catch
            {
                // best effort
            }
        }

        if (_popupViewModel is null)
        {
            _popupViewModel = new TrayPopupViewModel(Loom);
            WirePopup(_popupViewModel);
        }

        _popupViewModel.RefreshFromService();
        var window = new TrayPopupWindow(_popupViewModel);
        window.Closed += (_, _) => _popupWindow = null;
        _popupWindow = window;
        try
        {
            PositionPopup(window);
        }
        catch (Exception exception)
        {
            _popupWindow = null;
            try
            {
                window.Close();
            }
            catch
            {
                // best effort
            }

            _services.TryLogException(exception, "Tray popup failed");
        }
    }

    private void WirePopup(TrayPopupViewModel viewModel)
    {
        viewModel.ReportRequested += (_, _) => ShowReport(_lastStatus.LastReportPath);
        viewModel.SettingsRequested += (_, _) => ShowSettings();
        viewModel.ExitRequested += (_, _) => _services.RequestExit();
        viewModel.RollbackRequested += (_, _) => _ = RollbackAsync();
        viewModel.FirstRunRequested += (_, _) => ShowWizard();
    }

    /// <summary>
    /// Positions the flyout against the working area of the monitor that holds the cursor.
    /// All screen coordinates are device pixels (WinForms Screen/Cursor) and are converted to DIPs
    /// with the window's <see cref="PresentationSource"/> so 150%/200% DPI keeps it on-screen.
    /// </summary>
    private void PositionPopup(TrayPopupWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Opacity = 0;
        window.Show();
        window.UpdateLayout();

        var (cursor, workTopLeft, workBottomRight) = GetCursorAndWorkArea(window);
        var shadow = TrayPopupWindow.ShadowMargin;
        var cardWidth = window.ActualWidth > 0 ? window.ActualWidth - shadow * 2 : 360;
        var cardHeight = window.ActualHeight > 0 ? window.ActualHeight - shadow * 2 : 480;

        var alignRight = cursor.X > (workTopLeft.X + workBottomRight.X) / 2;
        var cardLeft = alignRight
            ? workBottomRight.X - cardWidth - 8
            : workTopLeft.X + 8;
        var cardTop = workBottomRight.Y - cardHeight - 8;

        window.Left = cardLeft - shadow;
        window.Top = cardTop - shadow;
        window.Opacity = 1;
        window.Activate();
    }

    /// <summary>
    /// Positions the context menu above the notification area, right-aligned to the cursor, then
    /// fades it in. Clamped to the cursor monitor's working area (multi-monitor + any taskbar edge).
    /// </summary>
    private void PositionMenu(TrayMenuWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Opacity = 0;
        window.Show();
        window.UpdateLayout();

        var (cursor, workTopLeft, workBottomRight) = GetCursorAndWorkArea(window);
        var shadow = TrayMenuWindow.ShadowMargin;
        const double gap = 8;
        var cardWidth = window.ActualWidth > 0 ? window.ActualWidth - shadow * 2 : 232;
        var cardHeight = window.ActualHeight > 0 ? window.ActualHeight - shadow * 2 : 300;

        var cardLeft = cursor.X - cardWidth + gap;
        var cardTop = cursor.Y - cardHeight - gap;
        if (cardTop < workTopLeft.Y + gap) cardTop = cursor.Y + gap;

        cardLeft = Math.Max(workTopLeft.X + gap, Math.Min(cardLeft, workBottomRight.X - cardWidth - gap));
        cardTop = Math.Max(workTopLeft.Y + gap, Math.Min(cardTop, workBottomRight.Y - cardHeight - gap));

        window.Left = cardLeft - shadow;
        window.Top = cardTop - shadow;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        window.BeginAnimation(UIElement.OpacityProperty, fade);
        window.Activate();
    }

    /// <summary>
    /// Cursor position + working area of the cursor monitor, converted to the DIP space WPF uses for
    /// <see cref="Window.Left"/>/<see cref="Window.Top"/>. The scale comes from the monitor DPI
    /// (PerMonitorV2), so mixed-DPI multi-monitor setups do not push the menu/flyout off-screen.
    /// </summary>
    private static (Point Cursor, Point WorkTopLeft, Point WorkBottomRight) GetCursorAndWorkArea(Window window)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;

        var scale = MonitorScaleForPoint(cursor.X, cursor.Y);
        if (scale <= 0)
        {
            // Fallback: window's own composition target (a shown window reports its monitor scale).
            scale = 1.0 / DeviceToDipFallback(window);
        }

        return (
            new Point(cursor.X / scale, cursor.Y / scale),
            new Point(area.Left / scale, area.Top / scale),
            new Point(area.Right / scale, area.Bottom / scale));
    }

    private static double DeviceToDipFallback(Window window)
    {
        try
        {
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget is not null)
            {
                return source.CompositionTarget.TransformFromDevice.M11;
            }
        }
        catch
        {
            // fall through: 100% DPI
        }

        return 1.0;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Monitor scale factor for a physical screen point (1.0 = 96 DPI, 0 = unavailable).</summary>
    private static double MonitorScaleForPoint(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return 0;
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) != 0) return 0;
            return dpiX > 0 ? dpiX / 96.0 : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void ShowSettings() => OnUi(ShowSettingsCore);

    private void ShowSettingsCore()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsViewModel = new SettingsViewModel(Loom);
        _settingsViewModel.Load(Loom.Config, Loom.GetStatus());
        _settingsViewModel.RestartRequested += (_, _) => _services.RequestExit();
        var window = new SettingsWindow(_settingsViewModel);
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _settingsViewModel = null;
            _ = RefreshStatusAsync();
        };
        _settingsWindow = window;
        ApplyAppIcon(window);
        window.Show();
        window.Activate();
    }

    public void ShowWizard() => OnUi(ShowWizardCore);

    private void ShowWizardCore()
    {
        if (_firstRunWindow is not null)
        {
            _firstRunWindow.Activate();
            return;
        }

        _firstRunViewModel = new FirstRunViewModel(Loom);
        var window = new FirstRunWindow(_firstRunViewModel);
        window.Closed += (_, _) =>
        {
            _firstRunWindow = null;
            _firstRunViewModel = null;
            _ = RefreshStatusAsync();
        };
        _firstRunWindow = window;
        ApplyAppIcon(window);
        window.Show();
        window.Activate();
    }

    public void ShowReport(string? path = null) => OnUi(() => ShowReportCore(path));

    private void ShowReportCore(string? path)
    {
        if (_reportWindow is not null)
        {
            _ = _reportWindow.ViewModel.LoadAsync(path);
            _reportWindow.Activate();
            return;
        }

        _reportViewModel = new ReportViewModel(Loom);
        _reportViewModel.RollbackRequested += (_, _) => _ = RollbackAsync();
        var window = new ReportWindow(_reportViewModel);
        window.Closed += (_, _) =>
        {
            _reportWindow = null;
            _reportViewModel = null;
        };
        _reportWindow = window;
        ApplyAppIcon(window);
        window.Show();
        window.Activate();
        _ = _reportViewModel.LoadAsync(path);
    }

    private async Task RollbackAsync()
    {
        try
        {
            var status = Loom.GetStatus();
            var detail = string.IsNullOrWhiteSpace(status.Detail) ? "" : "\n\n" + status.Detail;
            var confirm = MessageBox.Show(
                "将把 desktop profile 的插件配置还原到最近快照：\n" +
                "• 只写配置与依赖声明，不改安装目录\n" +
                "• 还原前先把现场备份到 restore-backup\n" +
                "• 冲突项保留当前内容并写入报告" + detail + "\n\n是否继续？",
                "Loom · 文件级还原",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            var result = await Loom.RestoreAsync(new RestoreRequest { DryRun = false, Force = false })
                .ConfigureAwait(true);

            if (result.Outcome == OperationOutcome.NeedsManual)
            {
                var messages = result.Messages.Count == 0 ? "缺少强证据或存在冲突。" : string.Join("\n", result.Messages);
                var force = MessageBox.Show(
                    "还原需要人工确认：\n" + messages + "\n\n强制应用文件级还原？",
                    "Loom · 需要确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (force == MessageBoxResult.Yes)
                {
                    result = await Loom.RestoreAsync(new RestoreRequest { DryRun = false, Force = true })
                        .ConfigureAwait(true);
                }
            }

            var succeeded = result.Outcome == OperationOutcome.Succeeded;
            Notify(new LoomNotification
            {
                EventId = "ui-rollback-" + result.OperationId,
                Title = succeeded ? "文件级还原完成" : "文件级还原未完成",
                Message = succeeded
                    ? $"已还原 {result.RestoredCount} 项，重启 DSH 后生效。"
                    : string.Join("；", result.Messages),
                Kind = succeeded ? NotificationKind.RestoreCompleted : NotificationKind.Failed,
                ReportPath = result.ReportPath,
                At = DateTimeOffset.Now,
            });

            if (succeeded && result.ReportPath is { Length: > 0 }) ShowReport(result.ReportPath);
            await Loom.RefreshStatusAsync().ConfigureAwait(true);
            UpdateStatus(Loom.GetStatus());
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "UI rollback failed");
            Notify(new LoomNotification
            {
                EventId = "ui-rollback-failed-" + Guid.NewGuid().ToString("N"),
                Title = "还原失败",
                Message = exception.Message,
                Kind = NotificationKind.Failed,
                At = DateTimeOffset.Now,
            });
        }
    }

    // ------------------------------------------------------------------ preview render

    /// <summary>
    /// Debug/smoke hook used by <c>loom-dsh ui render</c>: renders popup/settings/wizard/tray-menu in
    /// dark and light to PNG under <c>outputDir ?? Paths.DataRoot\ui-preview</c>, then restores the
    /// user's theme.
    /// </summary>
    public IReadOnlyList<string> RenderSnapshots(string? outputDir = null)
    {
        var results = new List<string>();
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => results.AddRange(RenderSnapshots(outputDir)));
            return results;
        }

        var directory = string.IsNullOrWhiteSpace(outputDir)
            ? Path.Combine(_services.Paths.DataRoot, "ui-preview")
            : outputDir!;
        Directory.CreateDirectory(directory);

        var requestedTheme = Loom.Config.Theme;
        var status = ResolveRenderStatus();
        try
        {
            foreach (var theme in new[] { "dark", "light" })
            {
                ThemeManager.Apply(theme);
                var background = ThemeManager.Brush("BgBrush");

                var popupViewModel = new TrayPopupViewModel(Loom);
                popupViewModel.Update(status);
                results.Add(UiPreviewRenderer.Render(
                    new TrayPopupView { DataContext = popupViewModel },
                    background,
                    Path.Combine(directory, $"popup-{theme}.png")));

                var settingsViewModel = new SettingsViewModel(Loom);
                settingsViewModel.Load(Loom.Config, status);
                results.Add(UiPreviewRenderer.Render(
                    new SettingsView { DataContext = settingsViewModel },
                    background,
                    Path.Combine(directory, $"settings-{theme}.png")));

                var firstRunViewModel = new FirstRunViewModel(Loom);
                firstRunViewModel.ApplyStatus(status);
                results.Add(UiPreviewRenderer.Render(
                    new FirstRunView { DataContext = firstRunViewModel },
                    background,
                    Path.Combine(directory, $"wizard-{theme}.png")));

                results.Add(UiPreviewRenderer.Render(
                    new TrayMenuView { DataContext = TrayMenuViewModel.CreatePreview() },
                    background,
                    Path.Combine(directory, $"tray-menu-{theme}.png")));
            }
        }
        finally
        {
            ThemeManager.Apply(requestedTheme);
        }

        return results;
    }

    private LoomStatus ResolveRenderStatus()
    {
        // Preview only: use the real installation (when the tray already resolved it) plus representative
        // snapshot numbers, so the render is deterministic and does not trigger DSH probing/snapshots.
        return TrayPopupViewModel.CreatePreviewStatus(Loom.GetStatus().Installation);
    }

    // ------------------------------------------------------------------ infrastructure

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            try
            {
                _dispatcher.BeginInvoke(action);
            }
            catch (TaskCanceledException)
            {
                // dispatcher shutting down
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            Loom.StatusChanged -= OnStatusChanged;
            Loom.NotificationRaised -= OnNotificationRaised;
        }
        catch
        {
        }

        try
        {
            _menuWindow?.Close();
            _menuWindow = null;
            _menuViewModel = null;
        }
        catch
        {
        }

        try
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
        catch
        {
        }

        _currentIcon?.Dispose();
        _currentIcon = null;
        TrayIconFactory.Destroy(_iconHandle);
        _iconHandle = IntPtr.Zero;
    }
}
