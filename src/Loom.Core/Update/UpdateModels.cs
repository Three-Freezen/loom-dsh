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
