using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Loom.Services;
using Loom.ViewModels;

// WPF/WinForms 同时启用时消除类型歧义
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;

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
        if (_viewModel is not null) _viewModel.ThemePreviewRequested += OnThemePreviewRequested;

        if (!_initialized)
        {
            _initialized = true;
            NavGeneral.IsChecked = true;
            ApplySection(0);
        }
    }

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
