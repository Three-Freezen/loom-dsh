using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Loom.Services;
using Loom.ViewModels;

// WPF/WinForms 同时启用：明确使用 WPF 的 MessageBox（与 App/TrayHost 一致）。
using MessageBox = System.Windows.MessageBox;

// WPF/WinForms 同时启用时消除类型歧义
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;

namespace Loom.Views;

/// <summary>Settings content page (sidebar + six sections). Rendered inside <see cref="SettingsWindow"/>.</summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;
    private bool _initialized;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplySection(GetActiveSection());
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.ThemePreviewRequested -= OnThemePreviewRequested;
        _viewModel = e.NewValue as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ThemePreviewRequested += OnThemePreviewRequested;
            // 回滚属于写操作：确认框留在 View 层（VM 不直接弹窗，便于测试与预览）。
            _viewModel.ConfirmRollback = ConfirmRollback;
        }

        if (!_initialized)
        {
            _initialized = true;
            NavGeneral.IsChecked = true;
            ApplySection(0);
        }
    }

    /// <summary>离线回滚的二次确认（默认“否”）。</summary>
    private static bool ConfirmRollback(string message) =>
        MessageBox.Show(
            message,
            "Loom · 离线回滚",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Selects a settings section (0 通用 … 5 关于). Useful for CLI deep links and previews.</summary>
    public void SelectSection(int index)
    {
        _initialized = true;
        var buttons = new[] { NavGeneral, NavBackup, NavCompat, NavNotify, NavStorage, NavAbout };
        var safe = Math.Clamp(index, 0, buttons.Length - 1);
        buttons[safe].IsChecked = true;
        ApplySection(safe);
    }

    private void OnThemePreviewRequested(object? sender, string theme) => ThemeManager.Apply(theme);

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ApplySection(GetIndex(sender as RadioButton));
    }

    private int GetIndex(RadioButton? button)
    {
        if (ReferenceEquals(button, NavBackup)) return 1;
        if (ReferenceEquals(button, NavCompat)) return 2;
        if (ReferenceEquals(button, NavNotify)) return 3;
        if (ReferenceEquals(button, NavStorage)) return 4;
        if (ReferenceEquals(button, NavAbout)) return 5;
        return 0;
    }

    private int GetActiveSection()
    {
        if (NavBackup.IsChecked == true) return 1;
        if (NavCompat.IsChecked == true) return 2;
        if (NavNotify.IsChecked == true) return 3;
        if (NavStorage.IsChecked == true) return 4;
        if (NavAbout.IsChecked == true) return 5;
        return 0;
    }

    private void ApplySection(int index)
    {
        var sections = new[] { SectionGeneral, SectionBackup, SectionCompat, SectionNotify, SectionStorage, SectionAbout };
        for (var i = 0; i < sections.Length; i++)
        {
            sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// P3-R6 / R6：把本控件的高度扩展到“更新与兼容”整页内容，供 <c>ui render</c> 生成
    /// <c>settings-compat-{dark,light}.png</c>。运行时窗口始终是 640 高（内容在 ScrollViewer 里滚动），
    /// 这里只用于离屏预览，不改变正常窗口布局。
    /// 做法：先按 940×640 完成一次布局拿到内容列宽，再以无限高测量 <c>SectionCompat</c>，
    /// 最后取 max(视口, 44 窗口控制行 + 内容高 + 底部留白)。
    /// </summary>
    /// <returns>实际设置的控件高度。</returns>
    public double FitToFullContentHeight(double minimumHeight = 640)
    {
        SelectSection(2);

        var width = double.IsNaN(Width) || Width <= 0 ? 940 : Width;
        var viewportHeight = double.IsNaN(Height) || Height <= 0 ? minimumHeight : Math.Max(Height, minimumHeight);

        // 第一次布局：让 ScrollViewer / 内容列拿到真实宽度（窗口边框 1 + 侧栏 208 + 内容左右 padding 28）。
        Measure(new Size(width, double.PositiveInfinity));
        Arrange(new Rect(0, 0, width, viewportHeight));
        UpdateLayout();

        var contentWidth = SectionCompat.ActualWidth > 1
            ? SectionCompat.ActualWidth
            : Math.Max(1, width - 208 - 56 - 2);
        SectionCompat.Measure(new Size(contentWidth, double.PositiveInfinity));
        var contentHeight = Math.Ceiling(SectionCompat.DesiredSize.Height);

        // 44 = 顶部窗口控制行；FooterBar 是 Auto 行（底部操作条），必须计入，否则 ScrollViewer 仍会
        // 出现滚动条并裁掉最后一张卡片的底部说明（R6 首轮渲染即此问题）。
        var footerHeight = FooterBar.ActualHeight > 0 ? Math.Ceiling(FooterBar.ActualHeight) : 64;
        var target = Math.Max(viewportHeight, 44 + contentHeight + footerHeight + 8);
        Height = target;
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
        return target;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            Window.GetWindow(this)?.DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null) window.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
