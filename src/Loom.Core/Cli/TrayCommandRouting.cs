using Loom.Core.Infrastructure;

namespace Loom.Core.Cli;

/// <summary>命令应该直连，还是交给正在运行的托盘命令队列。</summary>
public enum TrayCommandRoute
{
    /// <summary>当前进程直接执行（读命令，或数据根/单实例条件不满足）。</summary>
    Direct,

    /// <summary>提交给托盘队列执行（写命令，且托盘与 CLI 解析到同一个数据根）。</summary>
    Queue,
}

/// <summary>
/// 托盘运行时的命令路由规则（纯函数，便于单测）：
/// <list type="bullet">
///   <item>托盘未运行 → 直连；</item>
///   <item>托盘运行且 CLI 数据根与托盘一致 → 写命令（snapshot/check/restore/update/install-autostart/
///         uninstall-autostart/migrate-data/ui）一律走队列；读命令（status/report/doctor/version）可直连；</item>
///   <item>数据根不一致 → 直连（队列目录不同，提交过去托盘也收不到）。</item>
/// </list>
/// 数据根使用 <see cref="DataRootResolver"/> 解析，保证与托盘进程的选择规则一致；托盘的真实数据根由
/// <see cref="TrayRootHandshakeStore"/> 握手文件提供（P2-9）。
///
/// 二期 <c>upgrade</c> / <c>recovery</c> **特意不在队列里**：单包安装超时默认 15 分钟、包级恢复默认
/// 10 分钟，远超队列的默认等待上限（20 分钟只覆盖常规命令；排队期间客户端无法区分“正在安装”与
/// “托盘卡住”，且托盘队列执行器会另加自己的超时）。这两个命令直连，但**不再依赖“静默并发”**：
/// 直连前先用 <see cref="NeedsCrossProcessProbe"/> 判定，并以 <c>WaitOne(0)</c> 非阻塞探测跨进程
/// <c>OperationGate</c>；拿不到就返回 Busy(4)，拿到则一直持有到操作结束，与托盘操作严格串行。
/// </summary>
public static class TrayCommandRouting
{
    private static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "snapshot",
        "check",
        "restore",
        "update",
        "install-autostart",
        "uninstall-autostart",
        "migrate-data",
        "ui",
    };

    /// <summary>该命令是否会产生写入（profile/state/report/注册表/数据目录）。</summary>
    public static bool IsWriteCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) && WriteCommands.Contains(command.Trim());

    /// <summary>预计耗时可能超过队列等待上限的长操作（直连 + 跨进程门控，不入队）。</summary>
    public static bool IsLongOperation(string? command) =>
        !string.IsNullOrWhiteSpace(command)
        && (string.Equals(command.Trim(), "upgrade", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command.Trim(), "recovery", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 直连前是否必须先非阻塞探测跨进程门：
    /// <c>upgrade</c>（除 <c>--dry-run</c>）与 <c>recovery apply|rollback</c>（除 <c>--dry-run</c>）；
    /// <c>recovery plan</c> 只读，不探测。探测失败由调用方返回 <see cref="ExitCodes.Busy"/>。
    /// </summary>
    public static bool NeedsCrossProcessProbe(CliInvocation invocation)
    {
        if (invocation is null) return false;
        if (invocation.Error is not null || invocation.Help) return false;
        if (!IsLongOperation(invocation.Command)) return false;
        if (invocation.DryRun) return false;

        if (string.Equals(invocation.Command?.Trim(), "recovery", StringComparison.OrdinalIgnoreCase))
        {
            var subcommand = invocation.Positionals.Count > 0
                ? invocation.Positionals[0].Trim().ToLowerInvariant()
                : "";
            return subcommand is "apply" or "rollback";
        }

        return true;
    }

    /// <summary>
    /// P3-R8：本次调用是否只读（不写 profile/数据根）。只读调用不应因为托盘使用其他数据根被拒绝：
    /// <list type="bullet">
    ///   <item>显式 <c>--dry-run</c>（upgrade / recovery apply|rollback / restore 等）；</item>
    ///   <item><c>recovery plan</c>（只生成计划，绝不写文件/调用 CLI）。</item>
    /// </list>
    /// 写命令（upgrade 无 dry-run、recovery apply、snapshot/check/update 等）仍必须拒绝。
    /// </summary>
    public static bool IsReadOnlyInvocation(CliInvocation invocation)
    {
        if (invocation is null || invocation.Error is not null || invocation.Help) return false;

        var command = invocation.Command?.Trim().ToLowerInvariant() ?? "";
        if (invocation.DryRun
            && command is "upgrade" or "recovery" or "restore" or "migrate-data")
        {
            // 只读演练：upgrade/recovery apply|rollback/restore/migrate-data 的 --dry-run 都不写盘。
            // 注意 update 没有 dry-run 实现，不能因为带了 --dry-run 就放行 apply。
            return true;
        }

        if (command == "recovery")
        {
            var subcommand = invocation.Positionals.Count > 0
                ? invocation.Positionals[0].Trim().ToLowerInvariant()
                : "";
            return subcommand == "plan";
        }

        return false;
    }

    /// <summary>
    /// P2-9：托盘运行、握手显示托盘在用另一个数据根，而 CLI **没有**显式 <c>--data-root</c>，
    /// 且命令会写盘（含长操作）→ 直接拒绝（退出码 2 <see cref="ExitCodes.NeedsManual"/>）。
    /// 显式 <c>--data-root</c> 指向另一个目录是用户明确意图，允许直连（不会与托盘写同一目录）。
    /// P3-R8：只读/dry-run 调用直接放行（只读自己的数据根，不会与托盘写同一目录）。
    /// </summary>
    public static bool ShouldRejectDifferentRoot(
        CliInvocation invocation,
        TrayRootHandshake? handshake,
        string? baseDirectory = null)
    {
        if (invocation is null || handshake is null) return false;
        if (invocation.Error is not null || invocation.Help) return false;
        if (!invocation.RoutesThroughTray) return false;
        if (!string.IsNullOrWhiteSpace(invocation.DataRoot)) return false;
        if (IsReadOnlyInvocation(invocation)) return false;
        if (!IsWriteCommand(invocation.Command) && !IsLongOperation(invocation.Command)) return false;

        try
        {
            var cliRoot = DataRootResolver.Resolve(null, baseDirectory).Root;
            return !DataRootResolution.PathEquals(cliRoot, handshake.Root);
        }
        catch
        {
            // 路径非法时退回旧行为，让命令自己报错（绝不拒绝到无法继续）。
            return false;
        }
    }

    /// <summary>
    /// 判定一条 CLI 调用应直连还是排队。<paramref name="trayDataRoot"/> 是托盘进程实际使用的数据根
    /// （由 <see cref="TrayRootHandshakeStore"/> 握手解析，读不到时退回 <see cref="DataRootResolver"/>）。
    /// </summary>
    public static TrayCommandRoute Decide(
        CliInvocation invocation,
        bool trayRunning,
        string? trayDataRoot,
        string? baseDirectory = null)
    {
        if (invocation is null) return TrayCommandRoute.Direct;
        if (!trayRunning) return TrayCommandRoute.Direct;
        if (invocation.Error is not null || invocation.Help) return TrayCommandRoute.Direct;
        if (!invocation.RoutesThroughTray) return TrayCommandRoute.Direct;
        if (!IsWriteCommand(invocation.Command)) return TrayCommandRoute.Direct;
        if (string.IsNullOrWhiteSpace(trayDataRoot)) return TrayCommandRoute.Direct;

        try
        {
            var cliRoot = DataRootResolver.Resolve(invocation.DataRoot, baseDirectory).Root;
            return DataRootResolution.PathEquals(cliRoot, trayDataRoot)
                ? TrayCommandRoute.Queue
                : TrayCommandRoute.Direct;
        }
        catch
        {
            // 路径非法时退回直连，让 CLI 自己报错（绝不会把命令投到错误的数据目录）。
            return TrayCommandRoute.Direct;
        }
    }
}
