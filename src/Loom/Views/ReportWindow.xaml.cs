using System.Windows;
using Loom.ViewModels;

namespace Loom.Views;

/// <summary>Single report window.</summary>
public partial class ReportWindow : Window
{
    public ReportWindow(ReportViewModel viewModel)
    {
        InitializeComponent();
        Root.DataContext = viewModel;
        ViewModel = viewModel;
    }

    public ReportViewModel ViewModel { get; }
}
