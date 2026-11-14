using System.Windows;
using System.Windows.Input;
using Loom.ViewModels;

// WPF/WinForms 同时启用时消除类型歧义
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Loom.Views;

/// <summary>Single settings window (frameless, centered, rounded).</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _saved;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Root.DataContext = viewModel;
        _viewModel.Cancelled += OnCancelled;
        _viewModel.Saved += OnSaved;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _viewModel.Cancelled -= OnCancelled;
            _viewModel.Saved -= OnSaved;
            if (!_saved) _viewModel.RevertThemePreview();
        };
    }

    private void OnSaved(object? sender, EventArgs e) => _saved = true;

    private void OnCancelled(object? sender, EventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.RevertThemePreview();
            Close();
        }
    }
}
