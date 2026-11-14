namespace Loom.Core.Infrastructure;

/// <summary>
/// Install-shape facts for the running executable plus the fixed installer asset name used by the
/// (future) in-app update flow. This version does not contact the network — it only exposes the
/// contract that the update implementation will rely on, so the installer layout can stay stable.
/// Reference: DeskOrder's UpdateService (GitHub releases/latest + fixed Setup asset name + /SILENT /DIR).
/// </summary>
public sealed class AppInstallInfo
{
    /// <summary>Main executable file name shipped by the installer.</summary>
    public const string MainExecutableName = "loom-dsh.exe";

    /// <summary>Inno Setup uninstaller — the marker that distinguishes an installed build from a dev/unpacked one.</summary>
    public const string UninstallerFileName = DataRootResolver.UninstallerFileName;

    /// <summary>
    /// Fixed installer asset name (never versioned) so that
    /// <c>releases/latest/download/loom-dsh-Setup.exe</c> always resolves to the newest build.
    /// </summary>
    public const string SetupAssetName = "loom-dsh-Setup.exe";

    /// <summary>Directory containing the running executable.</summary>
    public string ExecutableDirectory { get; init; } = "";

    /// <summary>True when <c>unins000.exe</c> lives next to the executable (installed by Inno Setup).</summary>
    public bool IsInstalledBuild { get; init; }

    /// <summary>Install directory for an installed build; null for development/unpacked runs.</summary>
    public string? InstallDir => IsInstalledBuild ? ExecutableDirectory : null;

    /// <summary>Installed-build data root (<c>&lt;install&gt;\Data</c>); null when not installed.</summary>
    public string? DataRoot => IsInstalledBuild ? Path.Combine(ExecutableDirectory, DataRootResolver.DataDirectoryName) : null;

    /// <summary>Default install directory the interactive installer suggests (<c>%LOCALAPPDATA%\Programs\Loom</c>).</summary>
    public static string DefaultInstallDir => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "Loom");

    /// <summary>Build install info for a directory (tests pass a temp directory; production uses <see cref="ForProcess"/>).</summary>
    public static AppInstallInfo ForDirectory(string? baseDirectory = null)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory));
        return new AppInstallInfo
        {
            ExecutableDirectory = directory,
            IsInstalledBuild = DataRootResolver.IsInstalledBuildDirectory(directory),
        };
    }

    /// <summary>Install info for the current process.</summary>
    public static AppInstallInfo ForProcess() => ForDirectory(AppContext.BaseDirectory);

    /// <summary>
    /// Silent-upgrade arguments for the fixed-name installer: <c>/SILENT /DIR="&lt;current install dir&gt;"</c>.
    /// Inno Setup upgrades in place, keeps <c>&lt;install&gt;\Data</c> (not listed in [Files]) and auto-restarts
    /// the app through its [Run] entry. The future in-app updater downloads <see cref="SetupAssetName"/> and
    /// launches it with these arguments, then exits the current process.
    /// </summary>
    public static string BuildSilentUpgradeArguments(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new ArgumentException("installDir must not be empty", nameof(installDir));
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        return $"/SILENT /DIR=\"{directory}\"";
    }
}
