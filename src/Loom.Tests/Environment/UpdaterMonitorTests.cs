using Loom.Core.Environment;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

public sealed class UpdaterMonitorTests
{
    private static (EnvSandbox Sandbox, string Pending, DshInstallation Installation) Create()
    {
        var sandbox = new EnvSandbox();
        var updaterRoot = Path.Combine(sandbox.Root, "updater-cache");
        var pending = Path.Combine(updaterRoot, "pending");
        Directory.CreateDirectory(pending);
        var installation = sandbox.CreateInstallation();
        installation.UpdaterRoot = updaterRoot;
        return (sandbox, pending, installation);
    }

    private static string WriteInstaller(string pendingDir, string fileName, string content = "installer")
    {
        var path = Path.Combine(pendingDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WriteUpdateInfo(string pendingDir, string fileName, string sha512) =>
        File.WriteAllText(
            Path.Combine(pendingDir, "update-info.json"),
            $"{{\"fileName\":\"{fileName}\",\"sha512\":\"{sha512}\",\"isAdminRightsRequired\":false}}");

    [Fact]
    public async Task IgnoresStaleInstallerForTheCurrentVersion()
    {
        var (sandbox, pending, installation) = Create();
        using var _ = sandbox;
        WriteInstaller(pending, "temp-deepseek-harness-0.2.2-rc.1-win-x64.exe");
        WriteInstaller(pending, "deepseek-harness-0.2.2-rc.1-win-x64.exe");
        WriteUpdateInfo(pending, "deepseek-harness-0.2.2-rc.1-win-x64.exe", "sha512-stale");

        var monitor = new UpdaterMonitor(new RecordingLogger());
        var result = await monitor.FindPendingAsync(installation, "0.2.2-rc.1");

        Assert.Null(result);
    }

    [Fact]
    public async Task ReportsDownloadStartFromTempFile()
    {
        var (sandbox, pending, installation) = Create();
        using var _ = sandbox;
        WriteInstaller(pending, "temp-deepseek-harness-0.2.3-win-x64.exe");

        var monitor = new UpdaterMonitor(new RecordingLogger());
        var result = await monitor.FindPendingAsync(installation, "0.2.2-rc.1");

        Assert.NotNull(result);
        Assert.Equal("0.2.3", result!.TargetVersion);
        Assert.Equal("deepseek-harness-0.2.3-win-x64.exe", result.FileName);
        Assert.Equal("download", result.Stage);
        Assert.Null(result.Sha512);
        Assert.NotNull(result.Path);
    }

    [Fact]
    public async Task ReportsReadyInstallerWithSha512()
    {
        var (sandbox, pending, installation) = Create();
        using var _ = sandbox;
        WriteInstaller(pending, "deepseek-harness-0.2.4-win-x64.exe");
        WriteUpdateInfo(pending, "deepseek-harness-0.2.4-win-x64.exe", "sha512-abc");

        var monitor = new UpdaterMonitor(new RecordingLogger());
        var result = await monitor.FindPendingAsync(installation, "0.2.2-rc.1");

        Assert.NotNull(result);
        Assert.Equal("0.2.4", result!.TargetVersion);
        Assert.Equal("ready", result.Stage);
        Assert.Equal("sha512-abc", result.Sha512);
        Assert.EndsWith("deepseek-harness-0.2.4-win-x64.exe", result.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadyBeatsDownloadOfSameVersionAndHighestVersionWins()
    {
        var (sandbox, pending, installation) = Create();
        using var _ = sandbox;
        WriteInstaller(pending, "temp-deepseek-harness-0.2.4-win-x64.exe");
        WriteInstaller(pending, "deepseek-harness-0.2.4-win-x64.exe");
        WriteUpdateInfo(pending, "deepseek-harness-0.2.4-win-x64.exe", "sha512-024");
        WriteInstaller(pending, "temp-deepseek-harness-0.2.5-win-x64.exe");

        var monitor = new UpdaterMonitor(new RecordingLogger());
        var result = await monitor.FindPendingAsync(installation, "0.2.2-rc.1");

        Assert.NotNull(result);
        Assert.Equal("0.2.5", result!.TargetVersion);
        Assert.Equal("download", result.Stage);
    }

    [Fact]
    public async Task ReturnsCandidateWhenCurrentVersionIsNotParsable()
    {
        var (sandbox, pending, installation) = Create();
        using var _ = sandbox;
        WriteInstaller(pending, "temp-deepseek-harness-0.2.3-win-x64.exe");

        var monitor = new UpdaterMonitor(new RecordingLogger());
        var result = await monitor.FindPendingAsync(installation, "unknown");

        Assert.NotNull(result);
        Assert.Equal("0.2.3", result!.TargetVersion);
    }

    [Fact]
    public async Task MissingUpdaterRootReturnsNull()
    {
        using var sandbox = new EnvSandbox();
        var installation = sandbox.CreateInstallation();
        installation.UpdaterRoot = Path.Combine(sandbox.Root, "no-updater-cache");

        var monitor = new UpdaterMonitor(new RecordingLogger());
        var result = await monitor.FindPendingAsync(installation, "0.2.2-rc.1");

        Assert.Null(result);
    }
}
