using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Loom.ViewModels;

// WPF/WinForms 同时启用时消除类型歧义
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace Loom.Views;

/// <summary>
/// Frameless, always-on-top tray context menu (WPF-drawn instead of WinForms
/// <c>ContextMenuStrip</c>).
///
/// <para>
/// Invocation sequencing is the fix for the "打开状态面板 does nothing" bug: the clicked row only
/// marks a pending action and closes the menu window. The action is then queued on the dispatcher
/// from <see cref="OnClosed"/>, i.e. it runs <b>after</b> the menu window is gone. The previous
/// WinForms menu kept its own active native window alive while <c>ToolStripMenuItem.Click</c> ran,
/// so the WPF flyout was activated during the click and immediately deactivated (and closed) when
/// the menu tore down its focus.
/// </para>
///
/// <para>
/// <b>Foreground-lock hardening</b>: a tray click happens while explorer owns the foreground, so
/// Windows may refuse <see cref="SetForegroundWindow"/>/<c>Activate()</c>. When the window is not
/// active there is no <c>Deactivated</c> event (menu would hang) and Esc/arrow keys cannot arrive.
/// We therefore (a) explicitly request the foreground in <see cref="OnLoaded"/> and record the
/// result in <see cref="Diagnostics"/>, and (b) when activation failed, keep a mouse capture on the
/// menu content so an outside click is delivered to us and closes the menu
/// (<see cref="OnLostMouseCapture"/> / <see cref="OnRootPreviewMouseDown"/>). Keyboard shortcuts
/// remain unavailable while Windows denies activation — this is a documented limitation.
/// </para>
/// </summary>
public partial class TrayMenuWindow : Window
{
    /// <summary>Margin reserved for the card shadow; callers position the window with this offset.</summary>
    public const double ShadowMargin = 20;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Action<string>? _diagnosticSink;
    private readonly Func<IntPtr, bool> _foregroundSetter;
    private readonly Action _activationRetry;
    private readonly List<string> _diagnostics = new();
    private TrayMenuItem? _pending;
    private bool _closingFromInvoke;
    private bool _isClosing;
    private bool _hasActivated;

    public TrayMenuWindow(
        TrayMenuViewModel viewModel,
        Action<string>? diagnosticSink = null,
        Func<IntPtr, bool>? foregroundSetter = null,
        Action? activationRetry = null)
    {
        ViewModel = viewModel;
        _diagnosticSink = diagnosticSink;
        _foregroundSetter = foregroundSetter ?? DefaultForegroundSetter;
        _activationRetry = activationRetry ?? DefaultActivationRetry;
        InitializeComponent();
        Root.DataContext = viewModel;
        Root.ItemInvoked += OnItemInvoked;
        Root.PreviewMouseDown += OnRootPreviewMouseDown;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
    }

    public TrayMenuViewModel ViewModel { get; }

    /// <summary>True after the pending action has actually run (test/diagnostic hook).</summary>
    public bool InvocationExecuted { get; private set; }

    /// <summary>True once a row was invoked (the window was closed by a command, not by focus loss).</summary>
    public bool WasInvoked { get; private set; }

    /// <summary>Row whose action is queued until the window has closed (test/diagnostic hook).</summary>
    public TrayMenuItem? PendingInvocation => _pending;

    /// <summary>Activation / foreground / capture diagnostics, newest last (real-machine + harness hook).</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>True when a SetForegroundWindow attempt was made for this menu.</summary>
    public bool ForegroundRequested { get; private set; }

    /// <summary>Result of the last SetForegroundWindow attempt (false = Windows foreground lock refused).</summary>
    public bool ForegroundSet { get; private set; }

    /// <summary>True once the window really received activation (Deactivated dismissal is available).</summary>
    public bool HasActivated => _hasActivated;

    /// <summary>True when activation failed and the mouse-capture dismissal fallback is engaged.</summary>
    public bool ActivationFallbackActive { get; private set; }

    /// <summary>
    /// Pure decision used by <see cref="OnLostMouseCapture"/>: only an inactive, still-open menu that
    /// is not in the middle of an invoke/close falls back to closing on capture loss.
    /// </summary>
    public static bool ShouldDismissOnMouseCaptureLoss(bool isVisible, bool isActive, bool invoking, bool closing)
        => isVisible && !isActive && !invoking && !closing;

    /// <summary>Pure geometry helper: true when a mouse-down position is inside the menu card.</summary>
    public static bool IsPointInsideMenu(Point point, double width, double height)
        => point.X >= 0 && point.Y >= 0 && point.X <= width && point.Y <= height;

    /// <summary>
    /// Runs an enabled row: closes the menu synchronously, then executes the row action on the next
    /// dispatcher turn. Also used by the spike harness to simulate a click deterministically.
    /// </summary>
    public void InvokeItem(TrayMenuItem item)
    {
        if (_pending is not null || _closingFromInvoke) return;
        if (item.IsSeparator || !item.IsEnabled || item.Action is null) return;

        _pending = item;
        _closingFromInvoke = true;
        WasInvoked = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Log("hwnd ready");
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hasActivated = true;
        Log($"activated (active={IsActive})");
        if (ActivationFallbackActive) ReleaseCaptureFallback("window became active");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        ReleaseCaptureFallback("window closing");
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        base.OnClosed(e);

        var pending = _pending;
        _pending = null;
        if (pending?.Action is not Action action) return;

        // Background priority: the window (and its native HWND/focus) is fully torn down before the
        // action runs — the flyout opened here therefore cannot be deactivated by the closing menu.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            InvocationExecuted = true;
            action();
        }));
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_closingFromInvoke) Close();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        var invoking = _closingFromInvoke || _pending is not null;
        if (!ShouldDismissOnMouseCaptureLoss(IsVisible, IsActive, invoking, _isClosing)) return;

        Log("WARN: mouse capture lost while window is inactive -> closing menu (foreground-lock fallback)");
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.UpdateLayout();
        Root.FocusFirstItem();

        RequestForeground();

        // Give the window a chance to process activation (or fail to), then engage the capture
        // fallback only when the OS refused to activate us.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(EngageCaptureFallbackIfNeeded));
    }

    /// <summary>Explicitly asks Windows for the foreground and records whether it worked.</summary>
    private void RequestForeground()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                Log("WARN: hwnd unavailable; foreground request skipped");
                return;
            }

            ForegroundRequested = true;
            ForegroundSet = _foregroundSetter(handle);
            Log($"foreground={(ForegroundSet ? "set" : "denied")} active={IsActive}");

            if (!ForegroundSet)
            {
                try
                {
                    _activationRetry();
                }
                catch
                {
                    // best effort
                }

                Log($"retry Activate -> active={IsActive}");
            }
        }
        catch (Exception exception)
        {
            Log("WARN: foreground request failed: " + exception.GetType().Name);
        }
    }

    /// <summary>
    /// When the window never became active, Deactivated will never fire; capture the mouse inside
    /// the menu so the next click (anywhere) reaches us and can dismiss it.
    /// </summary>
    private void EngageCaptureFallbackIfNeeded()
    {
        if (_isClosing) return;
        if (IsActive)
        {
            Log("window is active; deactivation dismissal is available");
            return;
        }

        try
        {
            if (Mouse.Capture(Root, CaptureMode.SubTree))
            {
                ActivationFallbackActive = true;
                Log("WARN: window is inactive; mouse capture fallback engaged (Esc/arrow keys unavailable until activated)");
            }
            else
            {
                Log("WARN: mouse capture fallback failed");
            }
        }
        catch (Exception exception)
        {
            Log("WARN: mouse capture fallback failed: " + exception.GetType().Name);
        }
    }

    private void ReleaseCaptureFallback(string reason)
    {
        if (!ActivationFallbackActive) return;
        ActivationFallbackActive = false;
        try
        {
            if (Mouse.Captured is not null) Mouse.Capture(null);
        }
        catch
        {
            // best effort
        }

        Log("mouse capture released (" + reason + ")");
    }

    /// <summary>Fallback outside-click dismissal (only delivered when the capture fallback is active).</summary>
    private void OnRootPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ActivationFallbackActive || _isClosing || _closingFromInvoke || _pending is not null) return;

        var point = e.GetPosition(Root);
        if (IsPointInsideMenu(point, Root.ActualWidth, Root.ActualHeight)) return;

        Log("outside mouse-down while inactive -> closing menu");
        Close();
    }

    private void OnItemInvoked(object? sender, TrayMenuItem item) => InvokeItem(item);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Down:
                e.Handled = Root.MoveFocus(+1);
                break;
            case Key.Up:
                e.Handled = Root.MoveFocus(-1);
                break;
            case Key.Home:
            case Key.End:
                e.Handled = Root.MoveFocus(e.Key == Key.Home ? int.MinValue : int.MaxValue);
                break;
        }
    }

    private static bool DefaultForegroundSetter(IntPtr handle)
    {
        try
        {
            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    private void DefaultActivationRetry()
    {
        try
        {
            Activate();
        }
        catch
        {
            // best effort
        }
    }

    private void Log(string message)
    {
        _diagnostics.Add(message);
        Debug.WriteLine("[TrayMenu] " + message);
        try
        {
            _diagnosticSink?.Invoke(message);
        }
        catch
        {
            // a broken diagnostic sink must not break the menu
        }
    }
}
