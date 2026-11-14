using Loom.Core.Environment;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

[Collection("real-app-asar")]
public sealed class InstallerLocatorTests
{
    private static InstallerLocator Isolated(
        EnvSandbox sandbox,
        FakeProcessProbe? processes = null,
        bool includeRegistry = false,
        bool includeAppUpdateScan = false,
        IEnumerable<string>? extraCandidates = null,
        IEnumerable<string>? scanRoots = null) =>
        new(
            sandbox.Paths,
            new RecordingLogger(),
            processes ?? new FakeProcessProbe(),
            includeRegistry,
            includeAppUpdateScan,
            extraCandidates,
            scanRoots);

    private static LoomConfig ConfigFor(EnvSandbox sandbox) => new()
    {
        InstallDir = sandbox.InstallDir,
        ProfileDir = sandbox.ProfileDir,
    };

    [Fact]
    public async Task LocatesFromConfigAndValidatesInstallation()
    {
        using var sandbox = new EnvSandbox();

        var installation = await Isolated(sandbox).LocateAsync(ConfigFor(sandbox));

        Assert.NotNull(installation);
        Assert.Equal(sandbox.InstallDir, installation!.InstallDir, ignoreCase: true);
        Assert.True(installation.Found);
        Assert.Equal("config.installDir", installation.Source);
        Assert.Equal("0.2.2-rc.1", installation.AppVersion);
        // A fake zero-byte Electron executable cannot run the helper: version must stay
        // unknown rather than being guessed from other files.
        Assert.Equal("unknown", installation.DshVersion);
        Assert.Equal(sandbox.ProfileDir, installation.ProfileDir, ignoreCase: true);
        Assert.True(File.Exists(installation.CliPath));
        Assert.True(File.Exists(installation.AppAsar));
        Assert.NotNull(installation.UpdaterRoot);
    }

    [Theory]
    [InlineData(@"resources\runtime\primary-runtime\runtime.json")]
    [InlineData(@"resources\runtime\cli\bin\dsh.cmd")]
    [InlineData(@"resources\app.asar")]
    [InlineData("DeepSeek Harness.exe")]
    public async Task RejectsCandidatesMissingARequiredFile(string relativePath)
    {
        using var sandbox = new EnvSandbox();
        sandbox.DeleteInstallFile(relativePath);

        var installation = await Isolated(sandbox).LocateAsync(ConfigFor(sandbox));

        Assert.Null(installation);
    }

    [Fact]
    public async Task RejectsInvalidConfigAndDoesNotScanWhenFallbacksDisabled()
    {
        using var sandbox = new EnvSandbox();
        var config = new LoomConfig { InstallDir = Path.Combine(sandbox.Root, "not-a-dsh") };

        var installation = await Isolated(sandbox).LocateAsync(config);

        Assert.Null(installation);
    }

    [Fact]
    public async Task UsesProcessCandidatePath()
    {
        using var sandbox = new EnvSandbox();
        var processes = new FakeProcessProbe();
        processes.Processes.Add(new DshProcess
        {
            Pid = 4242,
            Name = "DeepSeek Harness",
            ExecutablePath = sandbox.InstallPath("DeepSeek Harness.exe"),
        });

        var installation = await Isolated(sandbox, processes).LocateAsync(new LoomConfig());

        Assert.NotNull(installation);
        Assert.Equal(sandbox.InstallDir, installation!.InstallDir, ignoreCase: true);
        Assert.Equal("process", installation.Source);
        Assert.Contains("", processes.RequestedInstallDirs); // locator asks for all name candidates first
    }

    [Fact]
    public async Task UsesAppUpdateYmlScanAsLastResort()
    {
        using var sandbox = new EnvSandbox();

        var installation = await Isolated(
                sandbox,
                includeAppUpdateScan: true,
                scanRoots: new[] { sandbox.Root })
            .LocateAsync(new LoomConfig());

        Assert.NotNull(installation);
        Assert.Equal(sandbox.InstallDir, installation!.InstallDir, ignoreCase: true);
        Assert.Equal("app-update.yml", installation.Source);
    }

    [Fact]
    public async Task ProfileDirOverrideWinsOverDshHome()
    {
        using var sandbox = new EnvSandbox();
        var overrideProfile = Path.Combine(sandbox.Root, "custom-profile");
        Directory.CreateDirectory(overrideProfile);

        var config = new LoomConfig
        {
            InstallDir = sandbox.InstallDir,
            DshHome = sandbox.DshHome,
            ProfileDir = overrideProfile,
        };
        var installation = await Isolated(sandbox).LocateAsync(config);

        Assert.NotNull(installation);
        Assert.Equal(overrideProfile, installation!.ProfileDir, ignoreCase: true);
        Assert.Equal(sandbox.DshHome, installation.DshHome, ignoreCase: true);
    }

    [Fact]
    public async Task DshHomeEnvironmentSelectsProfile()
    {
        using var sandbox = new EnvSandbox();
        var config = new LoomConfig { InstallDir = sandbox.InstallDir };
        var previous = System.Environment.GetEnvironmentVariable("DSH_HOME");
        try
        {
            System.Environment.SetEnvironmentVariable("DSH_HOME", sandbox.DshHome);

            var installation = await Isolated(sandbox).LocateAsync(config);

            Assert.NotNull(installation);
            Assert.Equal(
                Path.Combine(sandbox.DshHome, "profiles", "desktop"),
                installation!.ProfileDir,
                ignoreCase: true);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("DSH_HOME", previous);
        }
    }

    [Fact]
    public async Task DefaultProfileIsUnderUserProfileWhenNothingOverrides()
    {
        using var sandbox = new EnvSandbox();
        var config = new LoomConfig { InstallDir = sandbox.InstallDir, DshHome = null, ProfileDir = null };
        var previous = System.Environment.GetEnvironmentVariable("DSH_HOME");
        try
        {
            System.Environment.SetEnvironmentVariable("DSH_HOME", null);

            var installation = await Isolated(sandbox).LocateAsync(config);

            Assert.NotNull(installation);
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh",
                "profiles",
                "desktop");
            Assert.Equal(expected, installation!.ProfileDir, ignoreCase: true);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("DSH_HOME", previous);
        }
    }

    [Fact]
    public async Task LocatesRealInstallationThroughRegistry()
    {
        if (!RealDsh.IsInstalled) return;

        using var sandbox = new EnvSandbox();
        var installation = await Isolated(sandbox, includeRegistry: true, includeAppUpdateScan: false)
            .LocateAsync(new LoomConfig());

        // No Uninstall entry on some machines: nothing to assert then.
        if (installation is null) return;

        Assert.Equal(RealDsh.InstallDir, installation.InstallDir, ignoreCase: true);
        Assert.StartsWith("registry:", installation.Source);
    }

    [Fact]
    public async Task RealInstallationProbeReadsRuntimeVersionAndBaseBundles()
    {
        if (!RealDsh.IsInstalled) return;
        using var sandbox = new EnvSandbox();
        if (!sandbox.DeployHelper()) return;

        var config = new LoomConfig { InstallDir = RealDsh.InstallDir, ProfileDir = RealDsh.ProfileDir };
        var installation = await Isolated(sandbox).LocateAsync(config);

        Assert.NotNull(installation);
        var expectedAppVersion = RealDsh.ReadRealDshVersion();
        if (!string.IsNullOrEmpty(expectedAppVersion))
        {
            Assert.Equal(expectedAppVersion, installation!.AppVersion);
        }

        Assert.True(
            SemVersion.TryParse(installation!.DshVersion, out _),
            $"dshVersion should come from the official evaluator, got {installation.DshVersion}");
        Assert.Contains("@deepseek-ai/dsh-base", installation.BaseBundles);
        Assert.Equal(RealDsh.ProfileDir, installation.ProfileDir, ignoreCase: true);
    }
}
