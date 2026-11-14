using System.Windows.Controls;

// WPF/WinForms 同时启用时消除类型歧义
using UserControl = System.Windows.Controls.UserControl;

namespace Loom.Views;

/// <summary>Tray flyout content. Rendered both inside <see cref="TrayPopupWindow"/> and headless previews.</summary>
public partial class TrayPopupView : UserControl
{
    public TrayPopupView()
    {
        InitializeComponent();
    }
}
