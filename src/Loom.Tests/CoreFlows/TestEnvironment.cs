using System.Text;
using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Tests.CoreFlows;

/// <summary>
/// Throw-away sandbox under %TEMP%\loom-tests\&lt;guid&gt; holding a fake DSH_HOME, a fake install
/// directory and a Loom data root. The real profile / install directory are only ever read.
/// </summary>
public sealed class LoomSandbox : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public LoomSandbox(string dshVersion = "0.2.2-rc.1", string appVersion = "0.2.2-rc.1")
    {
        Root = Path.Combine(Path.GetTempPath(), "loom-tests", Guid.NewGuid().ToString("N"));
        DshHome = Path.Combine(Root, "dsh-home");
        ProfileDir = Path.Combine(DshHome, "profiles", "desktop");
        InstallDir = Path.Combine(Root, "install");
        DataRoot = Path.Combine(Root, "loom-data");
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(DataRoot);

        WriteFile(Path.Combine(InstallDir, "resources", "runtime", "primary-runtime", "runtime.json"),
            "{\n  \"desktopVersion\": \"" + appVersion + "\",\n  \"platform\": \"win32\",\n  \"arch\": \"x64\"\n}\n");
        WriteFile(Path.Combine(InstallDir, "resources", "runtime", "cli", "bin", "dsh.cmd"), "@echo off\r\nexit /b 0\r\n");
        WriteFile(Path.Combine(InstallDir, "resources", "app.asar"), "");
        WriteFile(Path.Combine(InstallDir, "DeepSeek Harness.exe"), "");

        Paths = new LoomPaths(DataRoot);
        Paths.EnsureDirectories();

        Installation = new DshInstallation
        {
            InstallDir = InstallDir,
            ProfileDir = ProfileDir,
            DshHome = DshHome,
            AppVersion = appVersion,
            DshVersion = dshVersion,
            CliPath = Path.Combine(InstallDir, "resources", "runtime", "cli", "bin", "dsh.cmd"),
            ElectronExe = Path.Combine(InstallDir, "DeepSeek Harness.exe"),
            AppAsar = Path.Combine(InstallDir, "resources", "app.asar"),
            Source = "test",
        };
    }

    public string Root { get; }
    public string DshHome { get; }
    public string ProfileDir { get; }
    public string InstallDir { get; }
    public string DataRoot { get; }
    public LoomPaths Paths { get; }
    public DshInstallation Installation { get; }

    public string ProfilePath(string fileName) => Path.Combine(ProfileDir, fileName);

    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    public void WriteProfileFile(string fileName, string content) => WriteFile(ProfilePath(fileName), content);

    public string ReadProfileFile(string fileName) => File.ReadAllText(ProfilePath(fileName), Encoding.UTF8);

    public void AddPlugin(string name, string version, string? enginesDsh = null, string? patchFile = "./cordis.patch.yml",
        string[]? clientInject = null, string[]? platforms = null)
    {
        var manifest = new JsonObject
        {
            ["name"] = name,
            ["version"] = version,
        };
        if (!string.IsNullOrEmpty(enginesDsh))
        {
            manifest["engines"] = new JsonObject { ["dsh"] = enginesDsh };
        }

        var dsh = new JsonObject();
        if (!string.IsNullOrEmpty(patchFile) || platforms is not null)
        {
            var bundle = new JsonObject();
            if (!string.IsNullOrEmpty(patchFile)) bundle["patch"] = patchFile;
            if (platforms is not null)
            {
                bundle["platforms"] = new JsonArray(platforms.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }

            dsh["bundle"] = bundle;
        }

        if (clientInject is { Length: > 0 })
        {
            dsh["client"] = new JsonObject
            {
                ["inject"] = new JsonArray(clientInject.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            };
        }

        if (dsh.Count > 0) manifest["dsh"] = dsh;
        var directory = Path.Combine(ProfileDir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
        WriteFile(Path.Combine(directory, "package.json"), manifest.ToJsonString());
        if (!string.IsNullOrEmpty(patchFile) && patchFile.StartsWith(".", StringComparison.Ordinal))
        {
            WriteFile(Path.Combine(directory, patchFile), "- id: " + name + "\n");
        }
    }

    public void WritePatch(string content) => WriteProfileFile(DshDefaults.PatchFile, content);

    public void WritePatchBackup(string fileName, string content) => WriteProfileFile(fileName, content);

    public void WriteCompatibility(string json) => WriteProfileFile(DshDefaults.CompatibilityFile, json);

    public void WriteCordisYml(string content = "[]\n") => WriteProfileFile(DshDefaults.CordisFile, content);

    public string ReadSnapshotText(string snapshotId)
        => File.ReadAllText(Path.Combine(Paths.SnapshotDir(snapshotId), "snapshot.json"), Encoding.UTF8);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best effort cleanup; the sandbox lives under %TEMP%
        }
    }
}

/// <summary>Small JSON/document builders used by several core-flow tests.</summary>
public static class TestData
{
    public static string PackageJson(IEnumerable<string> bundles, IReadOnlyDictionary<string, string>? dependencies = null)
    {
        var dependenciesNode = new JsonObject();
        if (dependencies is not null)
        {
            foreach (var pair in dependencies) dependenciesNode[pair.Key] = pair.Value;
        }

        var root = new JsonObject
        {
            ["name"] = "dsh-profile-desktop",
            ["private"] = true,
            ["dependencies"] = dependenciesNode,
            ["dsh"] = new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["bundles"] = new JsonArray(bundles.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                },
            },
        };
        return root.ToJsonString();
    }

    public static string Lockfile(IReadOnlyDictionary<string, string> versions, IReadOnlyDictionary<string, string>? specs = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("lockfileVersion: '9.0'");
        builder.AppendLine();
        builder.AppendLine("importers:");
        builder.AppendLine();
        builder.AppendLine("  .:");
        builder.AppendLine("    dependencies:");
        foreach (var pair in versions)
        {
            builder.AppendLine($"      {pair.Key}:");
            var spec = specs is not null && specs.TryGetValue(pair.Key, out var explicitSpec) ? explicitSpec : pair.Value;
            builder.AppendLine($"        specifier: {spec}");
            builder.AppendLine($"        version: {pair.Value}");
        }

        builder.AppendLine();
        builder.AppendLine("packages:");
        foreach (var pair in versions)
        {
            builder.AppendLine();
            builder.AppendLine($"  {pair.Key}@{pair.Value}:");
            builder.AppendLine($"    resolution: {{integrity: sha512-test-{pair.Key}-{pair.Value}}}");
        }

        return builder.ToString();
    }

    public static string PatchYaml(string marker = "ui-chat") =>
        "# patch\n- id: " + marker + "\n  name: \"@deepseek-ai/dsh-client-ui-chat\"\n  config:\n    transcriptView: standard\n";

    public static string CompatibilityJson(params (string Key, string[] Versions)[] entries)
    {
        var root = new JsonObject();
        foreach (var (key, versions) in entries)
        {
            root[key] = new JsonArray(versions.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }

        return root.ToJsonString();
    }

    public static SnapshotManifest Snapshot(
        string id,
        DateTimeOffset takenAt,
        IEnumerable<BundleSnapshot> bundles,
        IReadOnlyDictionary<string, DependencySnapshot>? dependencies = null,
        string? patchSha = null,
        IReadOnlyDictionary<string, List<string>>? exemptions = null,
        string? lockfileSha = null,
        string dshVersion = "0.2.2-rc.1",
        string appVersion = "0.2.2-rc.1")
    {
        var manifest = new SnapshotManifest
        {
            Id = id,
            TakenAt = takenAt,
            Trigger = SnapshotTrigger.PreUpdate,
            AppVersion = appVersion,
            DshVersion = dshVersion,
            ProfileDir = "",
            PackageArchive = new PackageArchive { Kind = "none" },
        };
        manifest.Profile.Bundles.AddRange(bundles);
        if (dependencies is not null)
        {
            foreach (var pair in dependencies) manifest.Profile.Dependencies[pair.Key] = pair.Value;
        }

        manifest.Profile.Patch = new PatchSnapshot { Sha256 = patchSha, File = DshDefaults.PatchFile };
        if (exemptions is not null)
        {
            manifest.Profile.Compatibility = new CompatibilitySnapshot
            {
                File = DshDefaults.CompatibilityFile,
                Sha256 = "compat-snapshot-sha",
                Exemptions = exemptions.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal),
            };
        }

        manifest.Profile.LockfileSha256 = lockfileSha;
        return manifest;
    }

    public static BundleSnapshot Bundle(string name, string? version = "1.0.0")
    {
        var bundleClass = DshDefaults.IsBaselineBundle(name)
            ? BundleClass.Baseline
            : DshDefaults.IsOfficialBundle(name)
                ? BundleClass.Official
                : BundleClass.ThirdParty;
        return new BundleSnapshot { Name = name, Class = bundleClass, Version = version };
    }

    public static ProfileState Profile(
        IEnumerable<string> bundles,
        IReadOnlyDictionary<string, string>? dependencies = null,
        string? patchSha = null,
        IEnumerable<string>? patchBackups = null,
        string? compatibilitySha = null,
        bool hasCompatibilityFile = false,
        string? lockfileSha = null,
        string profileDir = "",
        bool hasManifest = true)
    {
        var state = new ProfileState
        {
            ProfileDir = profileDir,
            HasManifest = hasManifest,
            PackageJsonText = hasManifest ? "{}" : null,
            PatchSha256 = patchSha,
            CompatibilitySha256 = compatibilitySha,
            LockfileSha256 = lockfileSha,
            PackageJsonPath = hasManifest ? Path.Combine(profileDir, DshDefaults.ManifestFile) : null,
        };
        state.Bundles.AddRange(bundles);
        if (dependencies is not null)
        {
            foreach (var pair in dependencies)
            {
                state.Dependencies[pair.Key] = new DependencySnapshot
                {
                    Spec = pair.Value,
                    Resolved = pair.Value.TrimStart('^', '~'),
                };
            }
        }

        if (patchBackups is not null) state.PatchBackups.AddRange(patchBackups);
        if (hasCompatibilityFile) state.CompatibilityPath = Path.Combine(profileDir, DshDefaults.CompatibilityFile);
        return state;
    }
}

/// <summary>Logger that keeps output out of the sandbox (tests must not write %LOCALAPPDATA%).</summary>
public sealed class NoopLogger : ILogger
{
    public void Log(LogLevel level, string message, string? operationId = null)
    {
    }

    public void LogException(Exception exception, string message, string? operationId = null)
    {
    }

    public string LogFilePath => "";

    public void Flush()
    {
    }
}

/// <summary>Configurable evaluator stub (env-adapters owns the real one).</summary>
public sealed class FakeEvaluator : ICompatibilityEvaluator
{
    public CompatibilityEngine Engine { get; set; } = CompatibilityEngine.Official;

    public Dictionary<string, CompatibilityLevel> Levels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StaticCheckResult> StaticResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Requirements { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Throw { get; set; }

    public Task<CompatibilityBatchResult> EvaluateAsync(
        DshInstallation installation,
        IReadOnlyList<PluginManifestInput> plugins,
        IReadOnlyDictionary<string, List<string>> exemptions,
        CancellationToken cancellationToken = default)
    {
        if (Throw) throw new InvalidOperationException("evaluator exploded");

        var batch = new CompatibilityBatchResult { Engine = Engine, EngineDetail = "fake" };
        foreach (var plugin in plugins)
        {
            var level = Levels.TryGetValue(plugin.Name, out var explicitLevel)
                ? explicitLevel
                : CompatibilityLevel.Compatible;
            var result = new PluginCompatibilityResult
            {
                Name = plugin.Name,
                Version = plugin.Version,
                Level = level,
                Compatible = level == CompatibilityLevel.Compatible,
                Static = new StaticPrecheck
                {
                    Result = StaticResults.TryGetValue(plugin.Name, out var staticResult)
                        ? staticResult
                        : StaticCheckResult.Ok,
                },
            };
            if (Requirements.TryGetValue(plugin.Name, out var requirements)) result.Requirements = requirements;
            if (level == CompatibilityLevel.Unknown) result.Error = "fake unknown";
            batch.Results.Add(result);
        }

        return Task.FromResult(batch);
    }
}

/// <summary>Copies the real desktop profile (read-only source) into the sandbox for a parse test.</summary>
public static class RealProfileCopy
{
    public static readonly string RealProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "desktop");

    public static bool TryCopyTo(string destination)
    {
        if (!Directory.Exists(RealProfileDir)) return false;
        Directory.CreateDirectory(destination);
        foreach (var fileName in new[]
                 {
                     DshDefaults.ManifestFile, DshDefaults.LockfileFile, DshDefaults.PatchFile,
                     DshDefaults.CordisFile, DshDefaults.CompatibilityFile,
                 })
        {
            var source = Path.Combine(RealProfileDir, fileName);
            if (File.Exists(source)) File.Copy(source, Path.Combine(destination, fileName), overwrite: true);
        }

        var nodeModules = Path.Combine(RealProfileDir, "node_modules");
        if (!Directory.Exists(nodeModules)) return true;
        foreach (var directory in Directory.EnumerateDirectories(nodeModules))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue; // .bin/.pnpm junctions
            if (name.StartsWith("@", StringComparison.Ordinal))
            {
                foreach (var scoped in Directory.EnumerateDirectories(directory))
                {
                    var manifest = Path.Combine(scoped, "package.json");
                    if (!File.Exists(manifest)) continue;
                    var target = Path.Combine(destination, "node_modules", name, Path.GetFileName(scoped));
                    Directory.CreateDirectory(target);
                    File.Copy(manifest, Path.Combine(target, "package.json"), overwrite: true);
                }

                continue;
            }

            var manifestPath = Path.Combine(directory, "package.json");
            if (!File.Exists(manifestPath)) continue;
            var targetDirectory = Path.Combine(destination, "node_modules", name);
            Directory.CreateDirectory(targetDirectory);
            File.Copy(manifestPath, Path.Combine(targetDirectory, "package.json"), overwrite: true);
        }

        return true;
    }
}
