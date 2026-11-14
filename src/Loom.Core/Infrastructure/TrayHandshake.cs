using System.Diagnostics;

namespace Loom.Core.Infrastructure;

/// <summary>
/// 托盘数据根握手（P2-9）：托盘启动时把“真实数据根 + pid + 启动时间 + 版本”写到**默认数据根**下的
/// <c>tray-root.json</c>，退出时删除；CLI 在没有显式 <c>--data-root</c> 时读它，从而知道运行中的托盘
/// 实际在用哪个目录。
///
/// 旧行为假设“托盘数据根 = CLI 进程解析出的默认根”。托盘以 <c>--data-root X</c> 启动时该假设不成立：
/// CLI 会把命令投到默认根的队列（托盘根本不读，等满队列超时），或在显式 <c>--data-root X</c> 时被
/// 判成“数据根不同”而直连并发写。握手文件放在默认根，是因为它必须能被“还不知道托盘根”的 CLI 找到。
/// </summary>
public sealed class TrayRootHandshake
{
    /// <summary>托盘进程 pid（用于判断握手是否过期）。</summary>
    public int Pid { get; set; }

    /// <summary>托盘实际使用的数据根（绝对路径）。</summary>
    public string Root { get; set; } = "";

    /// <summary>托盘启动时间（本地时间，仅用于诊断）。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>写握手时的工具版本。</summary>
    public string Version { get; set; } = "";
}

/// <summary>握手文件的读写（全部 best effort：握手失败绝不能影响托盘启动或 CLI 执行）。</summary>
public static class TrayRootHandshakeStore
{
    /// <summary>握手文件名。</summary>
    public const string FileName = "tray-root.json";

    /// <summary>默认数据根下的握手文件路径（CLI 侧固定的发现通道）。</summary>
    public static string DefaultFilePath => PathFor(DataRootResolver.Resolve(null).Root);

    /// <summary>指定数据根下的握手文件路径（单测用）。</summary>
    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, FileName);

    /// <summary>托盘启动时写握手：固定写默认数据根，内容记录托盘真实数据根。</summary>
    public static void Write(string trayDataRoot)
    {
        try
        {
            var defaultRoot = DataRootResolver.Resolve(null).Root;
            Directory.CreateDirectory(defaultRoot);
            var root = string.IsNullOrWhiteSpace(trayDataRoot)
                ? defaultRoot
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trayDataRoot));
            AtomicFile.WriteJson(PathFor(defaultRoot), new TrayRootHandshake
            {
                Pid = System.Environment.ProcessId,
                Root = root,
                StartedAt = DateTimeOffset.Now,
                Version = LoomPaths.ToolVersion,
            });
        }
        catch
        {
            // 握手是可选增强：写不进去时 CLI 退回“默认根”旧行为。
        }
    }

    /// <summary>托盘退出时删除握手；只删自己写的（pid 匹配），不误删另一个正在运行的托盘。</summary>
    public static void Delete()
    {
        try
        {
            var path = DefaultFilePath;
            var existing = TryReadFile(path);
            if (existing is not null && existing.Pid != System.Environment.ProcessId) return;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// 读取默认根下的握手并校验 pid 仍存活；文件不存在、损坏、pid 已退出或 root 非法 → null
    /// （调用方退回旧行为：把默认根当作托盘数据根）。
    /// </summary>
    public static TrayRootHandshake? TryReadLive()
    {
        var handshake = TryReadFile(DefaultFilePath);
        if (handshake is null) return null;
        if (!IsProcessAlive(handshake.Pid)) return null;
        if (string.IsNullOrWhiteSpace(handshake.Root)) return null;
        try
        {
            handshake.Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handshake.Root));
            return handshake;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取并解析握手文件（不校验 pid；单测直接用）。</summary>
    public static TrayRootHandshake? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return AtomicFile.ReadJsonSafe<TrayRootHandshake>(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>pid 对应的进程是否仍存活；无法确定时按“已退出”处理（保守：不让陈旧握手影响路由）。</summary>
    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
