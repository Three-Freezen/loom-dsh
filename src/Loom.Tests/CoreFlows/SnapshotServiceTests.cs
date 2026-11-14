using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Snapshot;
using Xunit;

namespace Loom.Tests.CoreFlows;

public sealed class SnapshotServiceTests
{
    private static readonly ILogger Logger = new NoopLogger();

    private static void SeedProfile(LoomSandbox sandbox)
    {
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
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(bundles, dependencies));
        sandbox.WriteProfileFile(DshDefaults.LockfileFile,
            TestData.Lockfile(new Dictionary<string, string> { ["dshmarket"] = "1.73.19", ["dsh-whale-widget"] = "0.4.2" }, dependencies));
        sandbox.WritePatch(TestData.PatchYaml());
        sandbox.WriteCordisYml();
        sandbox.AddPlugin("dshmarket", "1.73.19");
        sandbox.AddPlugin("dsh-whale-widget", "0.4.2");
    }

    [Fact]
    public async Task CaptureWriteLoadVerifyRoundtrip()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);

        var service = new SnapshotService(sandbox.Paths, Logger);
        var manifest = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.Manual, full: false);

        Assert.Equal(SnapshotTrigger.Manual, manifest.Trigger);
        Assert.Equal(5, manifest.Profile.Bundles.Count);
        Assert.Equal(BundleClass.Baseline, manifest.Profile.Bundles[0].Class);
        Assert.Equal(BundleClass.Official, manifest.Profile.Bundles[2].Class);
        Assert.Equal(BundleClass.ThirdParty, manifest.Profile.Bundles[3].Class);
        Assert.Equal(2, manifest.Plugins.Count);
        Assert.Equal("none", manifest.PackageArchive.Kind);
        Assert.Equal(DshDefaults.PatchFile, manifest.Files.CordisPatch);

        var reference = await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        Assert.Matches(@"^\d{8}-\d{6}-0\.2\.2-rc\.1-0\.2\.2-rc\.1", reference.Id);
        Assert.True(Directory.Exists(reference.Path));
        Assert.Equal(64, reference.Sha256.Length);
        Assert.Equal(2, reference.PluginCount);
        Assert.Equal(5, reference.BundleCount);
        Assert.True(reference.SizeBytes > 0);
        Assert.False(reference.Frozen);
        Assert.True(service.Verify(reference));

        // snapshot.json is UTF-8 without BOM
        var bytes = File.ReadAllBytes(Path.Combine(reference.Path, "snapshot.json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("\"schemaVersion\"", Encoding.UTF8.GetString(bytes));

        // profile configuration files were copied verbatim
        Assert.Equal(
            File.ReadAllText(sandbox.ProfilePath(DshDefaults.ManifestFile)),
            File.ReadAllText(Path.Combine(reference.Path, "profile", DshDefaults.ManifestFile)));

        var loaded = await service.LoadAsync(reference);
        Assert.NotNull(loaded);
        Assert.Equal(reference.Id, loaded!.Id);
        Assert.Equal(2, loaded.Plugins.Count);
        Assert.Equal("none", loaded.PackageArchive.Kind);

        var storage = service.InspectStorage(sandbox.Paths);
        Assert.Equal(1, storage.Count);
        Assert.True(storage.TotalBytes > 0);
    }

    [Fact]
    public async Task CorruptSnapshotFailsVerificationAndLoad()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var service = new SnapshotService(sandbox.Paths, Logger);
        var manifest = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.Manual, false);
        var reference = await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        Assert.True(service.Verify(reference));

        File.WriteAllText(Path.Combine(reference.Path, "snapshot.json"), "{ \"broken\": ", Encoding.UTF8);

        Assert.False(service.Verify(reference));
        Assert.Null(await service.LoadAsync(reference));
    }

    [Fact]
    public async Task FullSnapshotCopiesNodeModulesAndDowngradesWhenOverLimit()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var service = new SnapshotService(sandbox.Paths, Logger);

        var manifest = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.PreUpdate, full: true);
        var reference = await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        Assert.Equal("node-modules-tree", manifest.PackageArchive!.Kind);
        Assert.True(manifest.PackageArchive.TotalBytes > 0);
        Assert.Contains(manifest.PackageArchive.Files, file => file.Path.EndsWith("dshmarket/package.json", StringComparison.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(Path.Combine(reference.Path, "packages", "node_modules", "dshmarket")));

        // A node_modules tree larger than MaxArchiveBytes must downgrade to a light snapshot.
        var big = new byte[11 * 1024 * 1024];
        File.WriteAllBytes(
            Path.Combine(sandbox.ProfileDir, "node_modules", "dshmarket", "big.bin"), big);

        var downgraded = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.PreUpdate, full: true);
        var config = new LoomConfig { MaxArchiveBytes = 10L * 1024 * 1024 };
        var downgradedRef = await service.WriteAsync(downgraded, sandbox.Paths, config);
        Assert.Equal("none", downgraded.PackageArchive!.Kind);
        Assert.NotEmpty(service.LastWarnings);
        Assert.True(service.Verify(downgradedRef));
    }

    [Fact]
    public async Task RetentionKeepsFrozenLastRecentAndOnePerDshVersion()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var service = new SnapshotService(sandbox.Paths, Logger);
        var baseTime = DateTimeOffset.Now.AddHours(-2);

        async Task<SnapshotRef> Take(string id, int minutes, string dshVersion, SnapshotTrigger trigger)
        {
            var manifest = new SnapshotManifest
            {
                Id = id,
                TakenAt = baseTime.AddMinutes(minutes),
                Trigger = trigger,
                AppVersion = "0.2.2-rc.1",
                DshVersion = dshVersion,
                ProfileDir = sandbox.ProfileDir,
                DshHome = sandbox.DshHome,
                InstallDir = sandbox.InstallDir,
            };
            manifest.Profile.Bundles.Add(TestData.Bundle("@deepseek-ai/dsh-base"));
            return await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        }

        var frozen = await Take("s-frozen", -10, "0.1.9", SnapshotTrigger.PreUpdate);
        var s1 = await Take("s1", 10, "0.2.0", SnapshotTrigger.Auto);
        var s2 = await Take("s2", 20, "0.2.1", SnapshotTrigger.Auto);
        var s3 = await Take("s3", 30, "0.2.2", SnapshotTrigger.Auto);
        var s4 = await Take("s4", 40, "0.2.3", SnapshotTrigger.Auto);
        var s5 = await Take("s5", 50, "0.2.3", SnapshotTrigger.Auto);
        var s6 = await Take("s6", 60, "0.2.3", SnapshotTrigger.Auto);

        var state = new LoomState { LastSnapshot = s6, PreUpdateSnapshot = frozen };
        var config = new LoomConfig { KeepSnapshots = 2 };
        var removed = await service.RetainAsync(sandbox.Paths, state, config);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(sandbox.Paths.SnapshotDir("s4")) == false);
        foreach (var kept in new[] { "s-frozen", "s1", "s2", "s3", "s5", "s6" })
        {
            Assert.True(Directory.Exists(sandbox.Paths.SnapshotDir(kept)), kept + " should be kept");
        }
    }

    [Fact]
    public async Task RetentionEnforcesTotalQuotaWithoutDeletingProtectedSnapshots()
    {
        using var sandbox = new LoomSandbox();
        SeedProfile(sandbox);
        var service = new SnapshotService(sandbox.Paths, Logger);
        var baseTime = DateTimeOffset.Now.AddHours(-1);

        async Task<SnapshotRef> Take(string id, int minutes)
        {
            var manifest = new SnapshotManifest
            {
                Id = id,
                TakenAt = baseTime.AddMinutes(minutes),
                Trigger = SnapshotTrigger.Auto,
                AppVersion = "0.2.2-rc.1",
                DshVersion = "0.2.2-rc.1",
                ProfileDir = sandbox.ProfileDir,
            };
            manifest.Profile.Bundles.Add(TestData.Bundle("@deepseek-ai/dsh-base"));
            return await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        }

        var s1 = await Take("q1", 10);
        var s2 = await Take("q2", 20);
        var s3 = await Take("q3", 30);
        var s4 = await Take("q4", 40);

        var state = new LoomState { LastSnapshot = s4 };
        var removed = await service.RetainAsync(
            sandbox.Paths,
            state,
            new LoomConfig { KeepSnapshots = 5, MaxSnapshotBytes = 1 });

        Assert.Equal(3, removed);
        Assert.True(Directory.Exists(sandbox.Paths.SnapshotDir("q4")));
        Assert.False(Directory.Exists(sandbox.Paths.SnapshotDir("q1")));
        Assert.False(Directory.Exists(sandbox.Paths.SnapshotDir("q2")));
        Assert.False(Directory.Exists(sandbox.Paths.SnapshotDir("q3")));
    }

    [Fact]
    public async Task CaptureWithoutManifestThrows()
    {
        using var sandbox = new LoomSandbox();
        var service = new SnapshotService(sandbox.Paths, Logger);
        var exception = await Assert.ThrowsAsync<LoomException>(
            () => service.CaptureAsync(sandbox.Installation, SnapshotTrigger.Manual, false));
        Assert.Equal(LoomErrorCodes.NotFound, exception.Code);
    }
}
