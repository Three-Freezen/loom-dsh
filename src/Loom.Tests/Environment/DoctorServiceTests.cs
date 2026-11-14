using Loom.Core.Environment;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

public sealed class DoctorServiceTests
{
    [Fact]
    public async Task ReportsStableCheckKeysForASandboxInstallation()
    {
        using var sandbox = new EnvSandbox();
        var service = new DoctorService(sandbox.Paths, new RecordingLogger());
        var installation = sandbox.CreateInstallation();

        var result = await service.RunAsync(installation, sandbox.Paths, new LoomConfig());

        Assert.False(result.Healthy);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        var names = result.Checks.Select(check => check.Name).ToList();
        foreach (var expected in new[]
                 {
                     "installation", "profile", "runtime-manifest", "dsh-cli", "electron", "app-asar",
                     "profile-locks", "helper", "updater-cache", "data-root-writable", "disk-space",
                     "autostart", "version-consistency", "config",
                 })
        {
            Assert.Contains(expected, names);
        }

        Assert.True(result.Checks.Single(check => check.Name == "installation").Ok);
        Assert.True(result.Checks.Single(check => check.Name == "dsh-cli").Ok);
        Assert.False(result.Checks.Single(check => check.Name == "helper").Ok); // helper not deployed here
        Assert.True(result.Checks.Single(check => check.Name == "data-root-writable").Ok);
        Assert.True(result.Checks.Single(check => check.Name == "version-consistency").Ok);
        Assert.All(result.Checks, check => Assert.False(string.IsNullOrWhiteSpace(check.Detail)));
    }

    [Fact]
    public async Task MissingInstallationIsReportedAsUnhealthy()
    {
        using var sandbox = new EnvSandbox();
        var service = new DoctorService(sandbox.Paths, new RecordingLogger());

        var result = await service.RunAsync(null, sandbox.Paths, new LoomConfig());

        Assert.False(result.Healthy);
        var installationCheck = result.Checks.Single(check => check.Name == "installation");
        Assert.False(installationCheck.Ok);
        Assert.Contains("未找到", installationCheck.Detail);
    }

    [Fact]
    public async Task HelperCheckPassesWhenHelperIsDeployed()
    {
        using var sandbox = new EnvSandbox();
        if (!sandbox.DeployHelper()) return;
        var service = new DoctorService(sandbox.Paths, new RecordingLogger());

        var result = await service.RunAsync(sandbox.CreateInstallation(), sandbox.Paths, new LoomConfig());

        Assert.True(result.Checks.Single(check => check.Name == "helper").Ok);
    }
}
