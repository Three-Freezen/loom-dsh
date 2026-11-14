using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Loom.ViewModels;

// WPF/WinForms 同时启用时消除类型歧义
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Loom.Views;

/// <summary>
/// Frameless flyout anchored near the notification area. Closes on focus loss / Esc.
///
/// <para>
/// "打开状态面板" used to vanish instantly when opened from the tray menu: the WinForms
/// <c>ContextMenuStrip</c> item handler showed/activated this flyout while the menu still owned the
/// foreground window, then the closing menu deactivated it and <see cref="OnDeactivated"/> closed it.
/// The tray menu no longer runs commands before it closes (see <see cref="TrayMenuWindow"/>), and as
/// a second line of defence this window ignores deactivation for the first 300 ms and until it has
/// actually been activated at least once.
/// </para>
/// </summary>
public partial class TrayPopupWindow : Window
{
    /// <summary>Margin used by the card shadow; callers position the window with this offset.</summary>
    public const double ShadowMargin = 26;

    private static readonly TimeSpan DeactivateGuard = TimeSpan.FromMilliseconds(300);
    private readonly Stopwatch _opened = Stopwatch.StartNew();
    private bool _hasActivated;
    private bool _closing;

    public TrayPopupWindow(TrayPopupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Root.DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public TrayPopupViewModel ViewModel { get; }

    /// <summary>True once the flyout actually received activation (diagnostic/test hook).</summary>
    public bool HasActivated => _hasActivated;

    /// <summary>Closes the flyout unconditionally (used by the tray host when re-opening).</summary>
    public void ForceClose()
    {
        _closing = true;
        Close();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hasActivated = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_closing) return;
        if (!_hasActivated) return;
        if (_opened.Elapsed < DeactivateGuard) return;

        _closing = true;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ForceClose();
        }
    }
}
