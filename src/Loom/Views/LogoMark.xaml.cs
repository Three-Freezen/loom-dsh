using System.Windows.Controls;

// WPF/WinForms 同时启用时消除类型歧义
using UserControl = System.Windows.Controls.UserControl;

namespace Loom.Views;

/// <summary>Reusable LOOM mark (rounded tile + woven lines + check). Purely decorative.</summary>
public partial class LogoMark : UserControl
{
    public LogoMark()
    {
        InitializeComponent();
    }
}
