namespace Loom.Core.Update;

/// <summary>
/// In-app update flow state. The settings page renders it as a status card; the tray uses it for
/// balloon notifications and the CLI <c>update</c> command prints it.
/// </summary>
public enum UpdateState
{
    /// <summary>Nothing checked yet in this process.</summary>
    Idle,

    /// <summary>A version check is in flight.</summary>
    Checking,

    /// <summary>Checked and already on the newest published version.</summary>
    UpToDate,

    /// <summary>A newer version is available; waiting for the user to download it.</summary>
    Available,

    /// <summary>Downloading the fixed-name installer asset.</summary>
    Downloading,

    /// <summary>Installer downloaded and verified; ready to apply (silent upgrade + restart).</summary>
    Ready,

    /// <summary>The last operation failed (see <see cref="UpdateService.ErrorText"/>).</summary>
    Failed,

    /// <summary>The current process is a portable/dev build; in-app update is not supported.</summary>
    NotSupported,
}

/// <summary>
/// Result of one successful version probe. <see cref="Version"/> is the normalized remote version
/// (tag <c>v</c> prefix stripped), e.g. <c>0.2.0</c>.
/// </summary>
public sealed record UpdateCheckResult(string Version, string ReleaseUrl, string AssetUrl);

/// <summary>
/// 下载成功时写入数据根的“安装包旁车校验信息”（P2-R1）：跨进程 <c>update --apply</c> 不再依赖
/// 下载进程的内存状态，而是以此文件作为唯一信任来源。
/// 位置：<c>&lt;data-root&gt;\updates\loom-dsh-Setup.exe.meta.json</c>（不污染 Downloads 目录）。
/// </summary>
public sealed class VerifiedSetupMetadata
{
    /// <summary>下载完成时安装包的绝对路径（防止用 A 的旁车校验 B）。</summary>
    public string? SetupPath { get; set; }

    /// <summary>下载完成后实际文件内容的 SHA256（小写十六进制）。</summary>
    public string? Sha256 { get; set; }

    /// <summary>下载时发现的目标版本（如 <c>0.2.0</c>），执行前必须与安装包内嵌 ProductVersion 一致。</summary>
    public string? Version { get; set; }

    /// <summary>下载完成时的文件大小（字节）。</summary>
    public long Size { get; set; }

    /// <summary>下载完成时间。</summary>
    public DateTimeOffset DownloadedAt { get; set; }
}
