using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Loom.ViewModels;

// WPF/WinForms 同时启用时消除类型歧义
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace Loom.Views;

/// <summary>
/// Tray context-menu content: a themed card of 36px rows (WPF-drawn, not a WinForms context menu).
/// Rendered both inside <see cref="TrayMenuWindow"/> and headless previews.
/// Click handling only raises <see cref="ItemInvoked"/>; the window owns the
/// "close first, then run the action" sequencing (see <see cref="TrayMenuWindow"/>).
/// </summary>
public partial class TrayMenuView : UserControl
{
    public TrayMenuView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when an enabled, non-separator row is clicked.</summary>
    public event EventHandler<TrayMenuItem>? ItemInvoked;

    /// <summary>Moves keyboard focus to the first enabled row (called once the window is shown).</summary>
    public void FocusFirstItem()
    {
        foreach (var button in EnumerateRows())
        {
            if (!CanFocus(button)) continue;
            button.Focus();
            return;
        }
    }

    /// <summary>Moves focus to the next/previous enabled row (wraps; also from "nothing focused").</summary>
    public bool MoveFocus(int delta)
    {
        var candidates = EnumerateRows().Where(CanFocus).ToList();
        if (candidates.Count == 0) return false;

        var index = candidates.FindIndex(button => button.IsKeyboardFocused || button.IsKeyboardFocusWithin);
        int next;
        if (index < 0)
        {
            next = delta >= 0 ? 0 : candidates.Count - 1;
        }
        else
        {
            var offset = (int)((long)delta % candidates.Count);
            next = (int)(((index + offset) % candidates.Count + candidates.Count) % candidates.Count);
        }

        return candidates[next].Focus();
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrayMenuItem item }) return;
        if (!item.CanInvoke) return;
        e.Handled = true;
        ItemInvoked?.Invoke(this, item);
    }

    private static bool CanFocus(Button button)
        => button.Visibility == Visibility.Visible && button.IsEnabled;

    private IEnumerable<Button> EnumerateRows()
    {
        for (var index = 0; index < ItemsHost.Items.Count; index++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container)
            {
                continue;
            }

            var button = FindDescendant<Button>(container);
            if (button is not null) yield return button;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }

        return null;
    }
}
