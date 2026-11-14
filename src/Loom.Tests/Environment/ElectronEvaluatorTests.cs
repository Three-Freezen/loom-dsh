using Loom.Core.Evaluation;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

[Collection("real-app-asar")]
public sealed class ElectronEvaluatorTests : IDisposable
{
    private readonly EnvSandbox _sandbox = new();
    private readonly RecordingLogger _logger = new();
    private readonly bool _helperReady;

    public ElectronEvaluatorTests()
    {
        _helperReady = _sandbox.DeployHelper();
    }

    public void Dispose() => _sandbox.Dispose();

    private DshInstallation RealInstallation() => new()
    {
        InstallDir = RealDsh.InstallDir,
        ProfileDir = RealDsh.ProfileDir,
        DshHome = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".dsh"),
        AppAsar = RealDsh.AppAsar,
        ElectronExe = RealDsh.ElectronExe,
        CliPath = Path.Combine(RealDsh.InstallDir, @"resources\runtime\cli\bin\dsh.cmd"),
        AppVersion = RealDsh.ReadRealDshVersion() ?? "unknown",
        // Leave dshVersion unknown: the helper must read the official runtime version.
        DshVersion = "unknown",
    };

    private static PluginManifestInput Plugin(string name, string manifestJson, string dir = "") => new()
    {
        Name = name,
        Version = "",
        Dir = dir,
        ManifestJson = manifestJson,
    };

    private bool RealEnvironmentAvailable => _helperReady && RealDsh.IsInstalled;

    [Fact]
    public async Task EvaluatesTheThreeRealPluginsWithOfficialEngine()
    {
        if (!RealEnvironmentAvailable || !RealDsh.HasRealPlugins) return;

        var names = new[] { "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" };
        var plugins = names
            .Select(name => Plugin(name, RealDsh.ReadPluginManifest(name), Path.Combine(RealDsh.ProfileDir, "node_modules", name)))
            .ToList();
        var evaluator = new ElectronEvaluator(_sandbox.Paths, _logger);

        var batch = await evaluator.EvaluateAsync(
            RealInstallation(),
            plugins,
            new Dictionary<string, List<string>>());

        Assert.Equal(CompatibilityEngine.Official, batch.Engine);
        Assert.Contains("dsh-app-boot", batch.EngineDetail ?? "");
        Assert.Equal(3, batch.Results.Count);
        Assert.All(batch.Results, result => Assert.Equal(result.Level == CompatibilityLevel.Compatible, result.Compatible));

        var market = batch.Results.Single(result => result.Name == "dshmarket");
        Assert.Equal(CompatibilityLevel.Compatible, market.Level);
        Assert.Equal(StaticCheckResult.Ok, market.Static.Result);

        var whale = batch.Results.Single(result => result.Name == "dsh-whale-widget");
        Assert.Equal(CompatibilityLevel.Compatible, whale.Level);
        Assert.Equal(StaticCheckResult.Ok, whale.Static.Result);

        // Real finding on 0.2.2-rc.1: wallpaper-engine injects @deepseek-ai/dsh-client-runtime,
        // which is not shipped in the installation's runtime node_modules, so the static
        // precheck can only mark it unverified (never incompatible/unknown).
        var wallpaper = batch.Results.Single(result => result.Name == "dsh-plugin-wallpaper-engine");
        Assert.NotEqual(CompatibilityLevel.Incompatible, wallpaper.Level);
        Assert.NotEqual(CompatibilityLevel.Unknown, wallpaper.Level);
        if (wallpaper.Static.MissingRefs.Count > 0)
        {
            Assert.Equal(CompatibilityLevel.Unverified, wallpaper.Level);
            Assert.Contains(wallpaper.Static.MissingRefs, reference => reference.StartsWith("client:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task HelperProbeReportsOfficialRuntimeVersionAndBaseBundles()
    {
        if (!RealEnvironmentAvailable) return;

        var probe = await ElectronHelper.ProbeAsync(
            _sandbox.Paths.HelperPath,
            RealDsh.InstallDir,
            RealDsh.AppAsar,
            RealDsh.ProfileDir,
            TimeSpan.FromSeconds(30),
            _logger);

        Assert.NotNull(probe);
        Assert.Equal("official", probe!.Engine);
        Assert.True(SemVersion.TryParse(probe.RuntimeVersion, out _), $"runtimeVersion={probe.RuntimeVersion}");
        Assert.True(SemVersion.TryParse(probe.AppVersion, out _), $"appVersion={probe.AppVersion}");
        Assert.Contains("@deepseek-ai/dsh-base", probe.BaseBundles);
        Assert.Contains("@deepseek-ai/dsh-web-app", probe.BaseBundles);
    }

    [Fact]
    public async Task OfficialAndFallbackEnginesAgreeOnFixturesAndRealPlugins()
    {
        if (!RealEnvironmentAvailable) return;

        var runtimeVersion = await ReadRuntimeVersionAsync();
        if (runtimeVersion is null) return;

        var fixtures = BuildFixtures();
        if (RealDsh.HasRealPlugins)
        {
            foreach (var name in new[] { "dshmarket", "dsh-plugin-wallpaper-engine", "dsh-whale-widget" })
            {
                fixtures.Add(Plugin(name, RealDsh.ReadPluginManifest(name), Path.Combine(RealDsh.ProfileDir, "node_modules", name)));
            }
        }

        var exemptions = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["fx-exempted@1.0.0"] = new() { runtimeVersion },
        };
        var request = new HelperRequest
        {
            Mode = "evaluate",
            AppRoot = RealDsh.AppAsar,
            ProfileDir = RealDsh.ProfileDir,
            RuntimeVersion = runtimeVersion,
            Exemptions = exemptions,
            Plugins = fixtures.Select(fixture => new HelperPluginInput
            {
                Name = fixture.Name,
                Version = fixture.Version,
                Dir = fixture.Dir,
                ManifestJson = fixture.ManifestJson,
            }).ToList(),
        };

        var official = await ElectronHelper.RunAsync(
            _sandbox.Paths.HelperPath, RealDsh.InstallDir, CloneWithFallback(request, false), TimeSpan.FromSeconds(30), _logger);
        var fallback = await ElectronHelper.RunAsync(
            _sandbox.Paths.HelperPath, RealDsh.InstallDir, CloneWithFallback(request, true), TimeSpan.FromSeconds(30), _logger);

        Assert.NotNull(official);
        Assert.NotNull(fallback);
        Assert.Equal("official", official!.Engine);
        Assert.Equal("fallback", fallback!.Engine);

        var officialByName = official.Results.ToDictionary(result => result.Name!, StringComparer.Ordinal);
        var mismatches = new List<string>();
        foreach (var fallbackResult in fallback.Results)
        {
            if (!officialByName.TryGetValue(fallbackResult.Name!, out var officialResult))
            {
                mismatches.Add($"{fallbackResult.Name}: missing in official");
                continue;
            }

            if (officialResult.Level != fallbackResult.Level
                || officialResult.Static.Result != fallbackResult.Static.Result
                || !officialResult.Requirements.SequenceEqual(fallbackResult.Requirements)
                || officialResult.Exempted != fallbackResult.Exempted)
            {
                mismatches.Add(
                    $"{fallbackResult.Name}: official={officialResult.Level}/{officialResult.Static.Result} " +
                    $"fallback={fallbackResult.Level}/{fallbackResult.Static.Result}");
            }
        }

        Assert.Empty(mismatches);

        // Lock down the expected levels (verified against the shipped evaluator).
        var levels = fallback.Results.ToDictionary(result => result.Name!, result => result.Level, StringComparer.Ordinal);
        Assert.Equal("compatible", levels["engines-missing"]);
        Assert.Equal("incompatible", levels["engines-old"]);
        Assert.Equal("compatible", levels["engines-prerelease"]);
        Assert.Equal("incompatible", levels["peer-unmet"]);
        Assert.Equal("incompatible", levels["peer-optional-unmet"]);
        Assert.Equal("incompatible", levels["engines-workspace-star"]);
        Assert.Equal("compatible", levels["peer-workspace-caret"]);
        Assert.Equal("incompatible", levels["engines-invalid-range"]);
        Assert.Equal("unknown", levels["engines-illegal-type"]);
        Assert.Equal("unknown", levels["missing-name"]);
        Assert.Equal("compatible", levels["other-peer-ignored"]);
        Assert.Equal("compatible", levels["fx-exempted"]);
        Assert.Equal("incompatible", levels["fx-platforms-darwin"]);
        Assert.Equal("incompatible", levels["fx-patch-missing"]);
        Assert.Equal("compatible", levels["fx-patch-ok"]);
        Assert.Equal("compatible", levels["fx-client-good"]);
        Assert.Equal("unverified", levels["fx-client-missing"]);
        Assert.True(fallback.Results.Single(result => result.Name == "fx-exempted").Exempted);
    }

    [Fact]
    public async Task MissingHelperDegradesToUnknownWithoutThrowing()
    {
        if (File.Exists(_sandbox.Paths.HelperPath)) File.Delete(_sandbox.Paths.HelperPath);
        var evaluator = new ElectronEvaluator(_sandbox.Paths, _logger);

        var batch = await evaluator.EvaluateAsync(
            RealInstallation(),
            new[] { Plugin("some-plugin", "{\"name\":\"some-plugin\",\"version\":\"1.0.0\"}") },
            new Dictionary<string, List<string>>());

        Assert.Equal(CompatibilityEngine.Unknown, batch.Engine);
        var result = Assert.Single(batch.Results);
        Assert.Equal(CompatibilityLevel.Unknown, result.Level);
        Assert.False(result.Compatible);
        Assert.NotNull(result.Error);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string?> ReadRuntimeVersionAsync()
    {
        var probe = await ElectronHelper.ProbeAsync(
            _sandbox.Paths.HelperPath,
            RealDsh.InstallDir,
            RealDsh.AppAsar,
            RealDsh.ProfileDir,
            TimeSpan.FromSeconds(30),
            _logger);
        return probe?.RuntimeVersion;
    }

    private static HelperRequest CloneWithFallback(HelperRequest source, bool forceFallback) => new()
    {
        Mode = source.Mode,
        AppRoot = source.AppRoot,
        ProfileDir = source.ProfileDir,
        RuntimeVersion = source.RuntimeVersion,
        Exemptions = source.Exemptions,
        Plugins = source.Plugins,
        ForceFallback = forceFallback,
    };

    private List<PluginManifestInput> BuildFixtures()
    {
        var fixturesRoot = Path.Combine(_sandbox.Root, "fixtures");
        Directory.CreateDirectory(fixturesRoot);
        var list = new List<PluginManifestInput>
        {
            Plugin("engines-missing", """{"name":"engines-missing","version":"1.0.0"}"""),
            Plugin("engines-old", """{"name":"engines-old","version":"1.0.0","engines":{"dsh":"^1.0.0"}}"""),
            Plugin("engines-prerelease", """{"name":"engines-prerelease","version":"1.0.0","engines":{"dsh":"^0.2.0-rc.1"}}"""),
            Plugin("engines-workspace-star", """{"name":"engines-workspace-star","version":"1.0.0","engines":{"dsh":"workspace:*"}}"""),
            Plugin("engines-invalid-range", """{"name":"engines-invalid-range","version":"1.0.0","engines":{"dsh":"not-a-range"}}"""),
            Plugin("engines-illegal-type", """{"name":"engines-illegal-type","version":"1.0.0","engines":"x"}"""),
            Plugin("missing-name", """{"version":"1.0.0","engines":{"dsh":"^1.0.0"}}"""),
            Plugin("peer-unmet", """{"name":"peer-unmet","version":"1.0.0","peerDependencies":{"@deepseek-ai/dsh-client-ui-chat":"^1.0.0"}}"""),
            Plugin("peer-optional-unmet", """{"name":"peer-optional-unmet","version":"1.0.0","peerDependencies":{"@deepseek-ai/dsh-client-ui-chat":"^1.0.0"},"peerDependenciesMeta":{"@deepseek-ai/dsh-client-ui-chat":{"optional":true}}}"""),
            Plugin("peer-workspace-caret", """{"name":"peer-workspace-caret","version":"1.0.0","peerDependencies":{"@deepseek-ai/dsh-client-ui-chat":"workspace:^"}}"""),
            Plugin("other-peer-ignored", """{"name":"other-peer-ignored","version":"1.0.0","peerDependencies":{"react":"^99.0.0","@deepseek-ai/cordis":"^99.0.0"}}"""),
            Plugin("fx-exempted", """{"name":"fx-exempted","version":"1.0.0","engines":{"dsh":"^1.0.0"}}"""),
        };

        var patchOkDir = Path.Combine(fixturesRoot, "patch-ok");
        Directory.CreateDirectory(patchOkDir);
        File.WriteAllText(
            Path.Combine(patchOkDir, "cordis.patch.yml"),
            "- insert:\n    - id: fx-patch-ok\n      name: \"@deepseek-ai/dsh-client-ui-chat\"\n");
        list.Add(Plugin(
            "fx-patch-ok",
            """{"name":"fx-patch-ok","version":"1.0.0","dsh":{"bundle":{"patch":"./cordis.patch.yml"}}}""",
            patchOkDir));

        var patchMissingDir = Path.Combine(fixturesRoot, "patch-missing");
        Directory.CreateDirectory(patchMissingDir);
        list.Add(Plugin(
            "fx-patch-missing",
            """{"name":"fx-patch-missing","version":"1.0.0","dsh":{"bundle":{"patch":"./nope.yml"}}}""",
            patchMissingDir));

        var platformsDir = Path.Combine(fixturesRoot, "platforms-darwin");
        Directory.CreateDirectory(platformsDir);
        list.Add(Plugin(
            "fx-platforms-darwin",
            """{"name":"fx-platforms-darwin","version":"1.0.0","dsh":{"bundle":{"platforms":["darwin"]}}}""",
            platformsDir));

        var clientGoodDir = Path.Combine(fixturesRoot, "client-good");
        Directory.CreateDirectory(clientGoodDir);
        list.Add(Plugin(
            "fx-client-good",
            """{"name":"fx-client-good","version":"1.0.0","dsh":{"client":{"inject":["@deepseek-ai/dsh-client-ui-chat"]}}}""",
            clientGoodDir));

        var clientMissingDir = Path.Combine(fixturesRoot, "client-missing");
        Directory.CreateDirectory(clientMissingDir);
        list.Add(Plugin(
            "fx-client-missing",
            """{"name":"fx-client-missing","version":"1.0.0","dsh":{"client":{"inject":["@deepseek-ai/no-such-client"]}}}""",
            clientMissingDir));

        return list;
    }
}
