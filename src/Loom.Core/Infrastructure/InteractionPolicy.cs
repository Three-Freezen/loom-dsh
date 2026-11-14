namespace Loom.Core.Infrastructure;

/// <summary>
/// 界面交互策略（P2-6）：判断是否允许弹模态对话框，以及 UI 异常“不静默全吞”的提示节流。
/// 放在 Core 里是为了可单测（WPF 工程本身不进测试项目）。
/// </summary>
public static class DisplayPolicy
{
    /// <summary>
    /// 只有交互式用户会话才允许弹模态框：<c>Environment.UserInteractive == false</c>（服务化启动/
    /// 无桌面/断开的会话）或 Windows session 0（服务会话）一律只写日志——否则 MessageBox 会永久
    /// 阻塞 UI 线程，托盘表现为“卡死”。
    /// </summary>
    public static bool ShouldShowModal(bool userInteractive, int sessionId) =>
        userInteractive && sessionId != 0;
}

/// <summary>
/// UI 线程未处理异常计数（P2-6）：调用方每次都按 ERROR 记日志（不再静默全吞），
/// 达到阈值时只提示用户一次。
/// </summary>
public sealed class UiExceptionBudget
{
    private int _count;

    /// <summary>第 N 次异常时提示一次。默认 3。</summary>
    public int PromptThreshold { get; init; } = 3;

    /// <summary>已记录的 UI 异常数量。</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>记录一次异常；返回 true 表示“恰好达到阈值，应提示一次”。</summary>
    public bool Record()
    {
        var count = Interlocked.Increment(ref _count);
        return count == PromptThreshold;
    }
}
