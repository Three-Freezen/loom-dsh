using System.Security.Cryptography;
using System.Text;

namespace Loom.Core.Infrastructure;

/// <summary>
/// 跨进程原语的命名（P2-R2 / P3-R1）。名字必须**由数据根派生**：
/// <list type="bullet">
///   <item>不同数据根不会互相阻塞（旧实现所有 profile 共用一把门）；</item>
///   <item>共享/便携目录、计划任务与交互托盘只要使用同一数据根，就落到同一个名字；</item>
///   <item>只取 SHA256 前 16 位十六进制，名字长度固定、只含 <c>[0-9a-f]</c>，不会把长路径或
///         非法字符带进内核对象名（Windows 对象名最长 260 字符且不允许 <c>\</c>）。</item>
/// </list>
/// 仍保留 <c>Local\</c> 前缀：普通用户没有 <c>SeCreateGlobalPrivilege</c>，改用 <c>Global\</c>
/// 会让标准用户/受限会话直接创建失败。跨会话互斥由数据根下的 <c>FileShare.None</c> 文件锁补齐
/// （见 <see cref="OperationGate"/>）：文件锁天然按根隔离、跨会话有效、进程退出自动释放。
/// </summary>
internal static class ProcessNames
{
    private const string Prefix = "Local\\LoomForDsh-";
    private const int HashLength = 16;

    /// <summary>把数据根归一化为稳定字符串（绝对路径、去掉末尾分隔符、Windows 下忽略大小写）。</summary>
    public static string NormalizeDataRoot(string? dataRoot)
    {
        var root = string.IsNullOrWhiteSpace(dataRoot) ? LoomPaths.DefaultDataRoot : dataRoot.Trim();
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch
        {
            root = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return root.ToUpperInvariant();
    }

    /// <summary>数据根哈希前 16 位（小写十六进制）；输入再长/含中文也只得到 16 个安全字符。</summary>
    public static string DataRootHash(string? dataRoot)
    {
        var normalized = NormalizeDataRoot(dataRoot);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..HashLength];
    }

    /// <summary><c>Local\LoomForDsh-&lt;hash16&gt;-Operation-v1</c>。</summary>
    public static string OperationMutex(string? dataRoot) =>
        Prefix + DataRootHash(dataRoot) + "-Operation-v1";

    /// <summary><c>Local\LoomForDsh-&lt;hash16&gt;-Log-v1</c>。</summary>
    public static string LogMutex(string? dataRoot) =>
        Prefix + DataRootHash(dataRoot) + "-Log-v1";
}
