using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loom.Core.Abstractions;
using Loom.Core.Evaluation;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Microsoft.Win32;

namespace Loom.Core.Environment;

/// <summary>
/// Locates and validates the installed DeepSeek Harness desktop app.
/// Candidate order: config.InstallDir -> running "DeepSeek Harness" process path ->
/// HKCU/HKLM Uninstall registry -> app-update.yml directory inference.
/// A candidate is accepted only when runtime.json, cli\bin\dsh.cmd and app.asar exist.
/// The locator keeps no cache: the caller (LoomService) owns the 10s resolution cache.
/// </summary>
public sealed class InstallerLocator : IInstallerLocator
{
    private const string RuntimeManifestRelativePath = @"resources\runtime\primary-runtime\runtime.json";
    private const string CliRelativePath = @"resources\runtime\cli\bin\dsh.cmd";
    private const string ElectronRelativePath = @"DeepSeek Harness.exe";
    private const string AppAsarRelativePath = @"resources\app.asar";
    private const string AppUpdateRelativePath = @"resources\app-update.yml";
    private static readonly TimeSpan HelperProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex QuotedPath = new("\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly LoomPaths _paths;
    private readonly ILogger _logger;
    private readonly IProcessProbe _processes;
    private readonly bool _includeRegistry;
    private readonly bool _includeAppUpdateScan;
    private readonly IReadOnlyList<string>? _extraCandidates;
    private readonly IReadOnlyList<string>? _scanRoots;

    public InstallerLocator(LoomPaths paths, ILogger logger)
        : this(paths, logger, new ProcessProbe(logger))
    {
    }

    /// <summary>Test-oriented overload: lets callers inject process candidates and disable real-machine probes.</summary>
    public InstallerLocator(
        LoomPaths paths,
        ILogger logger,
        IProcessProbe processes,
        bool includeRegistry = true,
        bool includeAppUpdateScan = true,
        IEnumerable<string>? extraCandidates = null,
        IEnumerable<string>? scanRoots = null)
    {
        _paths = paths;
        _logger = logger;
        _processes = processes;
        _includeRegistry = includeRegistry;
        _includeAppUpdateScan = includeAppUpdateScan;
        _extraCandidates = extraCandidates?.ToList();
        _scanRoots = scanRoots?.ToList();
    }

    public async Task<DshInstallation?> LocateAsync(LoomConfig config, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (candidate, source) in EnumerateCandidates(config))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!seen.Add(candidate.Trim())) continue;

            try
            {
                var installation = await TryBuildAsync(candidate, source, config, cancellationToken).ConfigureAwait(false);
                if (installation is null) continue;

                _logger.Log(
                    LogLevel.Info,
                    $"DSH located via {source}: {installation.InstallDir} (appVersion={installation.AppVersion}, dshVersion={installation.DshVersion}, profile={installation.ProfileDir})");
                return installation;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Log(LogLevel.Debug, $"Candidate {candidate} ({source}) rejected: {exception.Message}");
            }
        }

        _logger.Log(LogLevel.Debug, "No valid DeepSeek Harness installation found.");
        return null;
    }

    public void Invalidate()
    {
        // The locator resolves on every call; caching is owned by the caller.
    }

    // ------------------------------------------------------------- candidates

    private IEnumerable<(string Directory, string Source)> EnumerateCandidates(LoomConfig config)
    {
        // 1. explicit config override
        if (!string.IsNullOrWhiteSpace(config.InstallDir))
        {
            yield return (config.InstallDir!, "config.installDir");
        }

        // 2. running "DeepSeek Harness" processes (all of them; path ownership is
        // validated later by the standard file checks).
        foreach (var process in SafeFindProcesses())
        {
            var path = process.ExecutablePath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            yield return (Path.GetDirectoryName(path!) ?? "", "process");
        }

        // 3. HKCU/HKLM Uninstall entries
        if (_includeRegistry)
        {
            foreach (var candidate in EnumerateRegistryCandidates()) yield return candidate;
        }

        // 4. app-update.yml directory inference (standard install roots / drive roots)
        if (_includeAppUpdateScan)
        {
            foreach (var candidate in EnumerateAppUpdateCandidates()) yield return candidate;
        }

        if (_extraCandidates is not null)
        {
            foreach (var candidate in _extraCandidates) yield return (candidate, "extra");
        }
    }

    private IReadOnlyList<DshProcess> SafeFindProcesses()
    {
        try
        {
            return _processes.FindDshProcesses("");
        }
        catch (Exception exception)
        {
            _logger.Log(LogLevel.Debug, $"Process candidate enumeration failed: {exception.Message}");
            return Array.Empty<DshProcess>();
        }
    }

    private IEnumerable<(string Directory, string Source)> EnumerateRegistryCandidates()
    {
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                // yield is not allowed inside try/catch, so collect then yield.
                var found = new List<(string Directory, string Source)>();
                RegistryKey? uninstallRoot = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    uninstallRoot = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallRoot is null) continue;

                    foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
                    {
                        RegistryKey? entry = null;
                        try
                        {
                            entry = uninstallRoot.OpenSubKey(subKeyName);
                            if (entry is null) continue;

                            var displayName = entry.GetValue("DisplayName") as string ?? "";
                            var installLocation = entry.GetValue("InstallLocation") as string ?? "";
                            if (!displayName.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
                                && !installLocation.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var directory = ResolveRegistryInstallDirectory(entry, installLocation);
                            if (string.IsNullOrWhiteSpace(directory)) continue;
                            if (!seen.Add(directory)) continue;
                            found.Add((directory, $"registry:{hive}/{view}"));
                        }
                        finally
                        {
                            entry?.Dispose();
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.Log(LogLevel.Debug, $"Registry scan failed ({hive}/{view}): {exception.Message}");
                }
                finally
                {
                    uninstallRoot?.Dispose();
                }

                foreach (var candidate in found) yield return candidate;
            }
        }
    }

    private static string? ResolveRegistryInstallDirectory(RegistryKey entry, string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            return installLocation.Trim().Trim('"');
        }

        foreach (var valueName in new[] { "UninstallString", "QuietUninstallString", "DisplayIcon" })
        {
            if (entry.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;
            var quoted = QuotedPath.Match(raw);
            var path = quoted.Success ? quoted.Groups[1].Value : raw.Split(' ')[0];
            if (string.IsNullOrWhiteSpace(path)) continue;
            path = path.Trim().Trim('"');
            try
            {
                if (File.Exists(path)) return Path.GetDirectoryName(path);
                if (Directory.Exists(path)) return path;
            }
            catch
            {
                // ignore malformed paths
            }
        }

        return null;
    }

    private IEnumerable<(string Directory, string Source)> EnumerateAppUpdateCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateScanRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            List<string> directories;
            try
            {
                if (!Directory.Exists(root)) continue;
                directories = new List<string> { root };
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    directories.Add(child);
                }
            }
            catch
            {
                continue;
            }

            // Prefer directories whose name mentions DeepSeek, then the rest.
            directories = directories
                .OrderByDescending(directory => Path.GetFileName(directory).Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var directory in directories)
            {
                bool looksInstalled;
                try
                {
                    looksInstalled = File.Exists(Path.Combine(directory, AppUpdateRelativePath))
                        && File.Exists(Path.Combine(directory, AppAsarRelativePath));
                }
                catch
                {
                    looksInstalled = false;
                }

                if (!looksInstalled) continue;
                if (!seen.Add(directory)) continue;
                yield return (directory, "app-update.yml");
            }
        }
    }

    private IEnumerable<string> EnumerateScanRoots()
    {
        if (_scanRoots is { Count: > 0 })
        {
            foreach (var root in _scanRoots) yield return root;
            yield break;
        }

        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return localAppData;
            yield return Path.Combine(localAppData, "Programs");
        }

        var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) yield return programFiles;

        var programFilesX86 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) yield return programFilesX86;

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool usable;
            try
            {
                usable = drive.IsReady && drive.DriveType == DriveType.Fixed;
            }
            catch
            {
                usable = false;
            }

            if (usable) yield return drive.RootDirectory.FullName;
        }
    }

    // ------------------------------------------------------------- validation

    private async Task<DshInstallation?> TryBuildAsync(
        string candidate,
        string source,
        LoomConfig config,
        CancellationToken cancellationToken)
    {
        var installDir = NormalizeInstallDirectory(candidate);
        if (installDir.Length == 0) return null;

        var runtimeManifest = Path.Combine(installDir, RuntimeManifestRelativePath);
        var cliPath = Path.Combine(installDir, CliRelativePath);
        var electronExe = Path.Combine(installDir, ElectronRelativePath);
        var appAsar = Path.Combine(installDir, AppAsarRelativePath);

        if (!File.Exists(runtimeManifest)) return null;
        if (!File.Exists(cliPath)) return null;
        if (!File.Exists(appAsar)) return null;
        if (!File.Exists(electronExe)) return null;

        var (profileDir, dshHome) = ResolveProfile(config);
        var appVersion = ReadDesktopVersion(runtimeManifest);

        var installation = new DshInstallation
        {
            InstallDir = installDir,
            ProfileDir = profileDir,
            DshHome = dshHome,
            CliPath = cliPath,
            ElectronExe = electronExe,
            AppAsar = appAsar,
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion!,
            DshVersion = "unknown",
            UpdaterRoot = ResolveUpdaterRoot(installDir),
            Source = source,
        };

        // Version probe through the Electron Node helper (official runtime version,
        // app.asar package.json version and installation base bundles). A missing or
        // broken helper degrades to "unknown" without guessing.
        var probe = await ElectronHelper.ProbeAsync(
                _paths.HelperPath,
                installDir,
                appAsar,
                profileDir,
                HelperProbeTimeout,
                _logger,
                cancellationToken)
            .ConfigureAwait(false);

        if (probe is not null)
        {
            if (IsSemanticVersion(probe.RuntimeVersion)) installation.DshVersion = probe.RuntimeVersion!;
            if (string.Equals(installation.AppVersion, "unknown", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(probe.AppVersion))
            {
                installation.AppVersion = probe.AppVersion!;
            }

            if (probe.BaseBundles.Count > 0)
            {
                installation.BaseBundles = probe.BaseBundles.ToList();
            }

            _logger.Log(
                LogLevel.Debug,
                $"Helper probe: engine={probe.Engine} runtime={probe.RuntimeVersion ?? "?"} app={probe.AppVersion ?? "?"} baseBundles={installation.BaseBundles.Count}");
        }
        else
        {
            _logger.Log(LogLevel.Warn, "Electron helper unavailable; dshVersion/appVersion fallback marked unknown.");
        }

        return installation;
    }

    private static string NormalizeInstallDirectory(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "";
        var value = candidate.Trim().Trim('"');
        try
        {
            // A candidate may be the executable itself, its directory, or an install root.
            if (File.Exists(value) && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = Path.GetDirectoryName(value) ?? value;
            }

            if (Path.GetFileName(value).Equals("resources", StringComparison.OrdinalIgnoreCase))
            {
                value = Path.GetDirectoryName(value) ?? value;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch
        {
            return "";
        }
    }

    private static (string ProfileDir, string DshHome) ResolveProfile(LoomConfig config)
    {
        string dshHome;
        if (!string.IsNullOrWhiteSpace(config.DshHome))
        {
            dshHome = config.DshHome.Trim().Trim('"');
        }
        else
        {
            var environment = System.Environment.GetEnvironmentVariable("DSH_HOME");
            dshHome = string.IsNullOrWhiteSpace(environment)
                ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".dsh")
                : environment.Trim().Trim('"');
        }

        var profileDir = !string.IsNullOrWhiteSpace(config.ProfileDir)
            ? config.ProfileDir!.Trim().Trim('"')
            : Path.Combine(dshHome, "profiles", DshDefaults.DesktopProfile);

        return (SafeFullPath(profileDir), SafeFullPath(dshHome));
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    private static string? ReadDesktopVersion(string runtimeManifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("desktopVersion", StringComparison.OrdinalIgnoreCase)) continue;
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
            }
        }
        catch
        {
            // unreadable/invalid runtime.json: version stays unknown (installation validation uses existence only)
        }

        return null;
    }

    private static bool IsSemanticVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && SemVersion.TryParse(version, out _);

    private string? ResolveUpdaterRoot(string installDir)
    {
        var folderName = DshDefaults.UpdaterFolderName;
        try
        {
            var appUpdate = Path.Combine(installDir, AppUpdateRelativePath);
            if (File.Exists(appUpdate))
            {
                foreach (var line in File.ReadLines(appUpdate))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("updaterCacheDirName:", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = trimmed["updaterCacheDirName:".Length..].Trim().Trim('\'', '"');
                    if (!string.IsNullOrWhiteSpace(value)) folderName = value;
                    break;
                }
            }
        }
        catch
        {
            // fall back to the default cache folder name
        }

        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, folderName);
    }
}
