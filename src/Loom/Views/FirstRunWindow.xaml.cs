using System.Windows;
using Loom.ViewModels;

namespace Loom.Views;

/// <summary>Single first-run wizard window.</summary>
public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _viewModel;

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Root.DataContext = viewModel;
        _viewModel.Completed += OnFinished;
        _viewModel.Later += OnFinished;
        Closed += (_, _) =>
        {
            _viewModel.Completed -= OnFinished;
            _viewModel.Later -= OnFinished;
        };
    }

    public FirstRunViewModel ViewModel => _viewModel;

    private void OnFinished(object? sender, EventArgs e) => Close();
}
