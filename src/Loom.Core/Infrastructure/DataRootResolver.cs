namespace Loom.Core.Infrastructure;

/// <summary>
/// Rule that selected the effective tool data root. Declaration order is priority order
/// (highest first) and is part of the documented contract (docs/安装与更新.md).
/// </summary>
public enum DataRootSource
{
    /// <summary><c>--data-root &lt;dir&gt;</c> / <c>LoomOptions.DataRoot</c> — highest priority, never auto-migrated.</summary>
    Explicit = 0,

    /// <summary>Portable mode: <c>&lt;exe&gt;\Data\portable.flag</c> exists → data lives next to the exe.</summary>
    Portable = 1,

    /// <summary>Installed build (<c>unins000.exe</c> next to the exe) → data lives in <c>&lt;install&gt;\Data</c>.</summary>
    Installed = 2,

    /// <summary>Development run / unpacked copy without marker → legacy <c>%LOCALAPPDATA%\Loom</c>.</summary>
    LegacyDefault = 3,
}

/// <summary>Immutable result of <see cref="DataRootResolver.Resolve"/>: the chosen root plus every input.</summary>
public sealed class DataRootResolution
{
    /// <summary>Effective tool data root (absolute, normalized).</summary>
    public string Root { get; init; } = "";

    /// <summary>Which rule won.</summary>
    public DataRootSource Source { get; init; }

    /// <summary>Directory the running executable lives in (<c>AppContext.BaseDirectory</c> by default).</summary>
    public string BaseDirectory { get; init; } = "";

    /// <summary><c>&lt;exe&gt;\Data</c> — the data root used by installed and portable builds.</summary>
    public string InstalledDataRoot { get; init; } = "";

    /// <summary><c>&lt;exe&gt;\Data\portable.flag</c> — portable-mode marker checked at startup.</summary>
    public string PortableFlagPath { get; init; } = "";

    /// <summary>Legacy root (<c>%LOCALAPPDATA%\Loom</c>) that migration copies from; test hook may override it.</summary>
    public string LegacyRoot { get; init; } = "";

    /// <summary>True when <c>unins000.exe</c> sits next to the running executable.</summary>
    public bool IsInstalledBuild { get; init; }

    /// <summary>True when the portable marker selected the root.</summary>
    public bool IsPortable => Source == DataRootSource.Portable;

    /// <summary>True when <see cref="Root"/> is exactly <c>&lt;exe&gt;\Data</c>.</summary>
    public bool UsesInstalledDataDirectory => PathEquals(Root, InstalledDataRoot);

    /// <summary>Short Chinese label for CLI/UI output.</summary>
    public string SourceLabel => Source switch
    {
        DataRootSource.Explicit => "显式指定（--data-root）",
        DataRootSource.Portable => "便携模式（<exe>\\Data）",
        DataRootSource.Installed => "安装版（<安装目录>\\Data）",
        _ => "开发默认（%LOCALAPPDATA%\\Loom）",
    };

    internal static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Resolves the tool data root. Priority (first match wins):
/// <list type="number">
///   <item><c>--data-root &lt;dir&gt;</c> (explicit override, behavior unchanged from earlier versions);</item>
///   <item><c>&lt;exe&gt;\Data\portable.flag</c> (portable mode chosen by the user);</item>
///   <item><c>unins000.exe</c> next to the exe (installed build) → <c>&lt;install&gt;\Data</c>;</item>
///   <item>fallback: legacy <c>%LOCALAPPDATA%\Loom</c> (development runs, bin\Debug, unpacked builds).</item>
/// </list>
/// Development directories never have <c>unins000.exe</c>, so <c>dotnet run</c> / test sandboxes keep the
/// historical <c>%LOCALAPPDATA%\Loom</c> behavior unless they explicitly opt in.
/// </summary>
public static class DataRootResolver
{
    /// <summary>Inno Setup writes this uninstaller into the install directory; its presence marks an installed build.</summary>
    public const string UninstallerFileName = "unins000.exe";

    /// <summary>Name of the data directory next to the executable (installed/portable builds).</summary>
    public const string DataDirectoryName = "Data";

    /// <summary>Marker file that forces portable mode for unpacked builds.</summary>
    public const string PortableFlagFileName = "portable.flag";

    /// <summary>
    /// Test-only override for the legacy root (used by sandboxed end-to-end tests to fake an old
    /// <c>%LOCALAPPDATA%\Loom</c> without touching the real user profile). Production never sets it.
    /// </summary>
    public const string LegacyRootEnvironmentVariable = "LOOM_LEGACY_DATA_ROOT";

    /// <summary>Legacy default root: <c>%LOCALAPPDATA%\Loom</c> (or the test-only env override).</summary>
    public static string LegacyDefaultRoot
    {
        get
        {
            var overrideRoot = System.Environment.GetEnvironmentVariable(LegacyRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                try
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(overrideRoot));
                }
                catch
                {
                    // Malformed test hook: fall through to the real legacy root.
                }
            }

            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Loom");
        }
    }

    /// <summary>Portable marker path for a given executable directory.</summary>
    public static string PortableFlagPathFor(string baseDirectory) =>
        Path.Combine(baseDirectory, DataDirectoryName, PortableFlagFileName);

    /// <summary>True when <c>unins000.exe</c> exists in <paramref name="baseDirectory"/>.</summary>
    public static bool IsInstalledBuildDirectory(string baseDirectory)
    {
        try
        {
            return File.Exists(Path.Combine(baseDirectory, UninstallerFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolve the effective data root for the current process (or a supplied executable directory in tests).</summary>
    public static DataRootResolution Resolve(string? explicitDataRoot = null, string? baseDirectory = null)
    {
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory));
        var dataRoot = Path.Combine(baseDir, DataDirectoryName);
        var portableFlag = Path.Combine(dataRoot, PortableFlagFileName);
        var legacyRoot = LegacyDefaultRoot;
        var installed = IsInstalledBuildDirectory(baseDir);

        if (!string.IsNullOrWhiteSpace(explicitDataRoot))
        {
            return new DataRootResolution
            {
                Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(explicitDataRoot)),
                Source = DataRootSource.Explicit,
                BaseDirectory = baseDir,
                InstalledDataRoot = dataRoot,
                PortableFlagPath = portableFlag,
                LegacyRoot = legacyRoot,
                IsInstalledBuild = installed,
            };
        }

        if (File.Exists(portableFlag))
        {
            return new DataRootResolution
            {
                Root = dataRoot,
                Source = DataRootSource.Portable,
                BaseDirectory = baseDir,
                InstalledDataRoot = dataRoot,
                PortableFlagPath = portableFlag,
                LegacyRoot = legacyRoot,
                IsInstalledBuild = installed,
            };
        }

        if (installed)
        {
            return new DataRootResolution
            {
                Root = dataRoot,
                Source = DataRootSource.Installed,
                BaseDirectory = baseDir,
                InstalledDataRoot = dataRoot,
                PortableFlagPath = portableFlag,
                LegacyRoot = legacyRoot,
                IsInstalledBuild = true,
            };
        }

        return new DataRootResolution
        {
            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot)),
            Source = DataRootSource.LegacyDefault,
            BaseDirectory = baseDir,
            InstalledDataRoot = dataRoot,
            PortableFlagPath = portableFlag,
            LegacyRoot = legacyRoot,
            IsInstalledBuild = false,
        };
    }
}
