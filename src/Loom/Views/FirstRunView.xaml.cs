using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Loom.ViewModels;

// WPF/WinForms 同时启用时消除类型歧义
using UserControl = System.Windows.Controls.UserControl;

namespace Loom.Views;

/// <summary>First-run wizard content: scope explanation, auto-located installation, autostart, first snapshot.</summary>
public partial class FirstRunView : UserControl
{
    public FirstRunView()
    {
        InitializeComponent();
    }

    private FirstRunViewModel? ViewModel => DataContext as FirstRunViewModel;

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.IsBusy) return;
        if (viewModel.SnapshotDone)
        {
            if (viewModel.FinishCommand.CanExecute(null)) viewModel.FinishCommand.Execute(null);
        }
        else if (viewModel.TakeSnapshotCommand.CanExecute(null))
        {
            viewModel.TakeSnapshotCommand.Execute(null);
        }
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
