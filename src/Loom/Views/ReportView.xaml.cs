using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// WPF/WinForms 同时启用时消除类型歧义
using UserControl = System.Windows.Controls.UserControl;

namespace Loom.Views;

/// <summary>Report window content: markdown report + retry / rollback / plugin-manager entry points.</summary>
public partial class ReportView : UserControl
{
    public ReportView()
    {
        InitializeComponent();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) Window.GetWindow(this)?.DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null) window.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
