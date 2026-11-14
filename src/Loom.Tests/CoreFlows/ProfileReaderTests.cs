using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Snapshot;
using Xunit;

namespace Loom.Tests.CoreFlows;

public sealed class ProfileReaderTests
{
    private static readonly ILogger Logger = new NoopLogger();

    [Fact]
    public void ReadsManifestLockfilePatchCompatibilityAndPluginManifests()
    {
        using var sandbox = new LoomSandbox();
        var dependencies = new Dictionary<string, string>
        {
            ["dshmarket"] = "^1.73.19",
            ["dsh-whale-widget"] = "0.4.2",
        };
        var bundles = new List<string>
        {
            "@deepseek-ai/dsh-base",
            "@deepseek-ai/dsh-web-app",
            "@deepseek-ai/dsh-experimental-agent-team-profile",
            "dshmarket",
            "dsh-whale-widget",
        };
        sandbox.WriteProfileFile(DshDefaults.ManifestFile,
            TestData.PackageJson(bundles, dependencies));
        sandbox.WriteProfileFile(DshDefaults.LockfileFile,
            TestData.Lockfile(
                new Dictionary<string, string> { ["dshmarket"] = "1.73.19", ["dsh-whale-widget"] = "0.4.2" },
                dependencies));
        sandbox.WritePatch(TestData.PatchYaml());
        sandbox.WriteCordisYml();
        sandbox.WriteCompatibility(TestData.CompatibilityJson(("dsh-whale-widget@0.4.2", new[] { "0.2.2-rc.1", "0.1.0" })));
        sandbox.AddPlugin("dshmarket", "1.73.19");
        sandbox.AddPlugin("dsh-whale-widget", "0.4.2", clientInject: new[] { "@deepseek-ai/dsh-client-runtime" });

        var backupMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        sandbox.WritePatchBackup(DshDefaults.PatchFile + ".bak-" + (backupMillis - 5000), "old-1\n");
        sandbox.WritePatchBackup(DshDefaults.PatchFile + ".bak-" + backupMillis, "old-2\n");

        var state = new ProfileReader(Logger).Read(sandbox.Installation, strict: false);

        Assert.True(state.HasManifest);
        Assert.Equal(bundles, state.Bundles);
        Assert.Equal(2, state.Dependencies.Count);
        Assert.Equal("^1.73.19", state.Dependencies["dshmarket"].Spec);
        Assert.Equal("1.73.19", state.Dependencies["dshmarket"].Resolved);
        Assert.StartsWith("sha512-test-dshmarket-1.73.19", state.Dependencies["dshmarket"].Integrity);
        Assert.False(string.IsNullOrEmpty(state.LockfileSha256));
        Assert.False(string.IsNullOrEmpty(state.PatchSha256));
        Assert.Equal(2, state.PatchBackups.Count);
        Assert.EndsWith("bak-" + backupMillis, state.PatchBackups[^1]);
        Assert.False(string.IsNullOrEmpty(state.CompatibilitySha256));
        Assert.True(state.FileSha256.ContainsKey(DshDefaults.CompatibilityFile));
        Assert.False(string.IsNullOrEmpty(state.CordisYmlSha256));

        Assert.Equal(2, state.Plugins.Count);
        var whale = Assert.Single(state.Plugins, plugin => plugin.Name == "dsh-whale-widget");
        Assert.Equal("0.4.2", whale.Version);
        Assert.Equal("registry", whale.Source);
        Assert.NotNull(whale.Dsh);
        Assert.True(whale.Dsh!.Bundle);
        Assert.NotNull(whale.Dsh.ClientInject);
        Assert.Contains(whale.Static.PatchFiles, path => path.EndsWith("cordis.patch.yml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidCompatibilityFileIsMarkedNotRewritable()
    {
        using var sandbox = new LoomSandbox();
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(new[] { "@deepseek-ai/dsh-base" }));
        sandbox.WriteCompatibility("{ \"not-a-version-key\": [\"0.2.2-rc.1\"] }");

        var state = new ProfileReader(Logger).Read(sandbox.Installation, strict: false);

        Assert.NotNull(state.CompatibilityPath);
        Assert.Null(state.CompatibilitySha256); // documented convention: path set + sha null = damaged, never overwrite
        Assert.True(state.FileSha256.ContainsKey(DshDefaults.CompatibilityFile));
    }

    [Fact]
    public void StrictReadWaitsForProfileLockAndThrowsBusy()
    {
        using var sandbox = new LoomSandbox();
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(new[] { "@deepseek-ai/dsh-base" }));
        sandbox.WriteProfileFile(DshDefaults.ProfileLockFile, System.Environment.ProcessId.ToString());

        var reader = new ProfileReader(Logger, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50));
        var exception = Assert.Throws<LoomException>(() => reader.Read(sandbox.Installation, strict: true));
        Assert.Equal(LoomErrorCodes.Busy, exception.Code);
    }

    [Fact]
    public void ParsesCopyOfRealDesktopProfile()
    {
        using var sandbox = new LoomSandbox();
        if (!RealProfileCopy.TryCopyTo(sandbox.ProfileDir)) return; // real profile not present on this machine

        var state = new ProfileReader(Logger).Read(sandbox.Installation, strict: false);

        Assert.True(state.HasManifest);
        Assert.Equal(6, state.Bundles.Count);
        Assert.Contains("@deepseek-ai/dsh-experimental-agent-team-profile", state.Bundles);
        Assert.Equal(3, state.Dependencies.Count);
        Assert.Equal(3, state.Plugins.Count);
        Assert.Contains(state.Plugins, plugin => plugin.Name == "dsh-plugin-wallpaper-engine");
        var wallpaper = Assert.Single(state.Plugins, plugin => plugin.Name == "dsh-plugin-wallpaper-engine");
        Assert.Equal("1.4.2", wallpaper.Version);
        Assert.False(string.IsNullOrEmpty(wallpaper.Engines?["dsh"]));
        Assert.False(string.IsNullOrEmpty(state.PatchSha256));
        Assert.False(string.IsNullOrEmpty(state.LockfileSha256));
        Assert.Equal("1.4.2", state.Dependencies["dsh-plugin-wallpaper-engine"].Resolved);
        Assert.False(string.IsNullOrEmpty(state.Dependencies["dsh-plugin-wallpaper-engine"].Integrity));
    }
}
