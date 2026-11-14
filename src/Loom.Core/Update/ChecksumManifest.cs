using System.Security.Cryptography;

namespace Loom.Core.Update;

/// <summary>
/// SHA256SUMS.txt 的解析与校验（P2-3）。支持常见的三种写法：
/// <list type="bullet">
///   <item><c>&lt;sha256&gt;  &lt;file&gt;</c>（sha256sum/发布脚本默认）；</item>
///   <item><c>&lt;sha256&gt; *&lt;file&gt;</c>（sha256sum -b 的二进制标记）；</item>
///   <item><c>&lt;file&gt;  &lt;sha256&gt;</c> 或裸 sha256 行（部分手工清单）。</item>
/// </list>
/// 只按文件名匹配（清单里可能带路径），大小写不敏感。
/// </summary>
public static class ChecksumManifest
{
    /// <summary>在清单里查找指定文件的 SHA256；找不到返回 null。</summary>
    public static string? FindSha256(string? manifest, string fileName)
    {
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(fileName)) return null;
        var wanted = Path.GetFileName(fileName);
        foreach (var rawLine in manifest.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                // 只有一行 hash（少见）：无法绑定文件名，忽略以免误判。
                continue;
            }

            var hash = parts[0].TrimStart('*');
            var name = parts[^1].TrimStart('*');
            if (!IsSha256(hash) && IsSha256(parts[^1]))
            {
                // `file  hash` 顺序
                hash = parts[^1];
                name = parts[0];
            }

            if (!IsSha256(hash)) continue;
            if (string.Equals(Path.GetFileName(name), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>计算文件的 SHA256（小写十六进制）。</summary>
    public static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ComputeSha256(stream);
    }

    /// <summary>计算流的 SHA256（小写十六进制）。</summary>
    public static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>SHA256 是否为 64 位十六进制。</summary>
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>把 hash 缩成日志可读的短形式（前 8 + 后 8）。</summary>
    public static string Short(string hash) =>
        hash.Length <= 20 ? hash : $"{hash[..8]}…{hash[^8..]}";
}
