using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

/// <summary>
/// Throw-away sandbox under %TEMP%\loom-env-tests\&lt;guid&gt;: a fake DSH installation,
/// a fake profile and a Loom data root. Real install/profile are only ever read.
/// </summary>
public sealed class EnvSandbox : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public EnvSandbox(string appVersion = "0.2.2-rc.1")
    {
        Root = Path.Combine(Path.GetTempPath(), "loom-env-tests", Guid.NewGuid().ToString("N"));
        InstallDir = Path.Combine(Root, "DeepSeek");
        DshHome = Path.Combine(Root, "dsh-home");
        ProfileDir = Path.Combine(DshHome, "profiles", "desktop");
        DataRoot = Path.Combine(Root, "loom-data");
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(DataRoot);

        WriteRuntimeManifest(appVersion);
        Directory.CreateDirectory(Path.Combine(InstallDir, "resources", "runtime", "cli", "bin"));
        WriteFile(Path.Combine(InstallDir, "resources", "runtime", "cli", "bin", "dsh.cmd"), "@echo off\r\nexit /b 0\r\n");
        WriteFile(Path.Combine(InstallDir, "resources", "app.asar"), "");
        WriteFile(Path.Combine(InstallDir, "resources", "app-update.yml"), "provider: generic\nchannel: nightly\nupdaterCacheDirName: '@deepseek-aidsh-desktop-updater'\n");
        WriteFile(Path.Combine(InstallDir, "DeepSeek Harness.exe"), "");
        WriteFile(Path.Combine(ProfileDir, "package.json"), "{\n  \"name\": \"dsh-profile-desktop\",\n  \"private\": true\n}\n");

        Paths = new LoomPaths(DataRoot);
        Paths.EnsureDirectories();
    }

    public string Root { get; }
    public string InstallDir { get; }
    public string DshHome { get; }
    public string ProfileDir { get; }
    public string DataRoot { get; }
    public LoomPaths Paths { get; }

    public void WriteRuntimeManifest(string appVersion)
    {
        WriteFile(
            Path.Combine(InstallDir, "resources", "runtime", "primary-runtime", "runtime.json"),
            "{\n  \"desktopVersion\": \"" + appVersion + "\",\n  \"platform\": \"win32\",\n  \"arch\": \"x64\"\n}\n");
    }

    public void DeleteInstallFile(string relativePath)
    {
        var path = Path.Combine(InstallDir, relativePath);
        if (File.Exists(path)) File.Delete(path);
    }

    public string InstallPath(string relativePath) => Path.Combine(InstallDir, relativePath);

    public string ProfilePath(string relativePath) => Path.Combine(ProfileDir, relativePath);

    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    public void WriteProfileFile(string relativePath, string content) => WriteFile(ProfilePath(relativePath), content);

    public void WritePlugin(string relativeDirectory, string manifestJson, params (string RelativePath, string Content)[] extraFiles)
    {
        var directory = ProfilePath(Path.Combine("node_modules", relativeDirectory));
        WriteFile(Path.Combine(directory, "package.json"), manifestJson);
        foreach (var (relativePath, content) in extraFiles)
        {
            WriteFile(Path.Combine(directory, relativePath), content);
        }
    }

    public DshInstallation CreateInstallation() => new()
    {
        InstallDir = InstallDir,
        ProfileDir = ProfileDir,
        DshHome = DshHome,
        AppVersion = "0.2.2-rc.1",
        DshVersion = "0.2.2-rc.1",
        CliPath = InstallPath(@"resources\runtime\cli\bin\dsh.cmd"),
        ElectronExe = InstallPath("DeepSeek Harness.exe"),
        AppAsar = InstallPath(@"resources\app.asar"),
        Source = "test",
    };

    /// <summary>Deploys helper.mjs to this sandbox's data root (what HelperBootstrapper does in production).</summary>
    public bool DeployHelper()
    {
        var source = HelperSource.Find();
        if (source is null) return false;
        Directory.CreateDirectory(Paths.HelperDir);
        File.Copy(source, Paths.HelperPath, overwrite: true);
        return true;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best effort under %TEMP%
        }
    }
}

/// <summary>Locates the helper.mjs source shipped with the Loom project.</summary>
public static class HelperSource
{
    public static string? Find()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "helper", "helper.mjs");
        if (File.Exists(fromOutput)) return fromOutput;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var prefix in new[] { "", "src", Path.Combine("Loom-DSH", "src") })
            {
                var candidate = Path.Combine(directory.FullName, prefix, "Loom", "Assets", "helper.mjs");
                if (File.Exists(candidate)) return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>Logger that records lines in memory and never writes to %LOCALAPPDATA%.</summary>
public sealed class RecordingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate) return _lines.ToList();
        }
    }

    public string LogFilePath => "";

    public void Log(LogLevel level, string message, string? operationId = null)
    {
        lock (_gate) _lines.Add($"[{level}] {message}");
    }

    public void LogException(Exception exception, string message, string? operationId = null)
    {
        lock (_gate) _lines.Add($"[Error] {message} :: {exception.GetType().Name}: {exception.Message}");
    }

    public void Flush()
    {
    }

    public bool Contains(string text)
    {
        lock (_gate) return _lines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Controllable process probe for lock/locator tests.</summary>
public sealed class FakeProcessProbe : IProcessProbe
{
    public Func<int, bool> Alive { get; set; } = _ => false;

    public List<DshProcess> Processes { get; } = new();

    public List<string> RequestedInstallDirs { get; } = new();

    public IReadOnlyList<DshProcess> FindDshProcesses(string installDir)
    {
        RequestedInstallDirs.Add(installDir);
        return Processes;
    }

    public bool IsProcessAlive(int pid) => Alive(pid);

    public bool HasAny(string processName) => false;
}

/// <summary>Facts about the real machine, used to keep read-only integration tests honest.</summary>
public static class RealDsh
{
    public static string InstallDir => @"D:\DeepSeek";

    public static string AppAsar => Path.Combine(InstallDir, "resources", "app.asar");

    public static string ElectronExe => Path.Combine(InstallDir, "DeepSeek Harness.exe");

    public static string ProfileDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "desktop");

    public static bool IsInstalled =>
        File.Exists(Path.Combine(InstallDir, "resources", "runtime", "primary-runtime", "runtime.json"))
        && File.Exists(Path.Combine(InstallDir, "resources", "runtime", "cli", "bin", "dsh.cmd"))
        && File.Exists(AppAsar);

    public static bool HasRealPlugins =>
        File.Exists(Path.Combine(ProfileDir, "node_modules", "dshmarket", "package.json"))
        && File.Exists(Path.Combine(ProfileDir, "node_modules", "dsh-plugin-wallpaper-engine", "package.json"))
        && File.Exists(Path.Combine(ProfileDir, "node_modules", "dsh-whale-widget", "package.json"));

    public static string ReadPluginManifest(string packageName) =>
        File.ReadAllText(Path.Combine(ProfileDir, "node_modules", packageName, "package.json"), Encoding.UTF8);

    public static string? ReadRealDshVersion()
    {
        try
        {
            var runtimeJson = Path.Combine(InstallDir, "resources", "runtime", "primary-runtime", "runtime.json");
            var text = File.ReadAllText(runtimeJson);
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("desktopVersion", out var value) ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Serializes tests that run the real Electron helper / real app.asar.</summary>
[CollectionDefinition("real-app-asar", DisableParallelization = true)]
public sealed class RealAppAsarCollection
{
}
