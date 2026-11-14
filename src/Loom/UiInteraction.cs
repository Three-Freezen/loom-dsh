using System.Diagnostics;
using System.Windows;
using Loom.Core.Infrastructure;
using MessageBox = System.Windows.MessageBox;

namespace Loom;

/// <summary>
/// 托盘/界面侧的“安全弹窗”封装（P2-6）：非交互会话（服务化启动、断开的 RDP、session 0）只写日志，
/// 绝不弹模态框——模态框在无人点掉的会话里会永久阻塞 UI 线程。
/// </summary>
internal static class UiInteraction
{
    /// <summary>当前会话是否允许弹模态对话框。</summary>
    public static bool CanShowModal
    {
        get
        {
            try
            {
                return DisplayPolicy.ShouldShowModal(
                    Environment.UserInteractive,
                    Process.GetCurrentProcess().SessionId);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>弹一条警告（仅交互会话；任何失败都吞掉，绝不因为提示失败而崩溃）。</summary>
    public static void ShowWarning(string message, string title)
    {
        if (!CanShowModal) return;
        try
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // best effort
        }
    }
}
