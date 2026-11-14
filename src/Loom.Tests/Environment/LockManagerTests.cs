using Loom.Core.Environment;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

public sealed class LockManagerTests
{
    private static readonly int OwnPid = Environment.ProcessId;

    private static LockManager CreateManager() =>
        new(new FakeProcessProbe { Alive = pid => pid == OwnPid }, new RecordingLogger());

    private static DshInstallation InstallationFor(EnvSandbox sandbox) => sandbox.CreateInstallation();

    private static void WriteLock(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void DetectsLiveProfileLock()
    {
        using var sandbox = new EnvSandbox();
        WriteLock(sandbox.ProfilePath("lock"), OwnPid + "\n");

        var status = CreateManager().Inspect(InstallationFor(sandbox));

        Assert.True(status.Busy);
        Assert.Contains(OwnPid, status.Pids);
        Assert.Contains(status.Reasons, reason => reason.Contains("profile\\lock", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoresExitedProfileLock()
    {
        using var sandbox = new EnvSandbox();
        WriteLock(sandbox.ProfilePath("lock"), "999999\n");

        var status = CreateManager().Inspect(InstallationFor(sandbox));

        Assert.False(status.Busy);
        Assert.NotNull(status.Detail);
        Assert.Contains("已退出", status.Detail);
    }

    [Fact]
    public void DetectsLiveManifestLockAndNotesThe120SecondWindow()
    {
        using var sandbox = new EnvSandbox();
        var lockPath = sandbox.ProfilePath("package.json.lock");
        WriteLock(lockPath, OwnPid + "\n");
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddSeconds(-200));

        var status = CreateManager().Inspect(InstallationFor(sandbox));

        Assert.True(status.Busy);
        Assert.Contains(status.Reasons, reason => reason.Contains("package.json.lock", StringComparison.Ordinal));
        Assert.NotNull(status.Detail);
        Assert.Contains("120s", status.Detail);
    }

    [Fact]
    public void DetectsLiveRunRecordAndIgnoresExitedOne()
    {
        using var sandbox = new EnvSandbox();
        var runRecord = sandbox.ProfilePath(Path.Combine(".plugin-manager", "run.json"));

        WriteLock(runRecord, "{\"pid\":" + OwnPid + ",\"grouped\":false}\n");
        var live = CreateManager().Inspect(InstallationFor(sandbox));
        Assert.True(live.Busy);
        Assert.Contains(OwnPid, live.Pids);

        WriteLock(runRecord, "{\"pid\":999999,\"grouped\":false}\n");
        var dead = CreateManager().Inspect(InstallationFor(sandbox));
        Assert.False(dead.Busy);
        Assert.Contains("run.json", dead.Detail);
    }

    [Fact]
    public void UnparsableRunRecordIsTreatedAsBusy()
    {
        using var sandbox = new EnvSandbox();
        WriteLock(sandbox.ProfilePath(Path.Combine(".plugin-manager", "run.json")), "{not json");

        var status = CreateManager().Inspect(InstallationFor(sandbox));

        Assert.True(status.Busy);
        Assert.Contains(status.Reasons, reason => reason.Contains("run.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitForQuietTimesOutWhileProfileLockIsHeld()
    {
        using var sandbox = new EnvSandbox();
        WriteLock(sandbox.ProfilePath("lock"), OwnPid + "\n");

        var result = await CreateManager().WaitForQuietAsync(
            InstallationFor(sandbox),
            TimeSpan.FromMilliseconds(300));

        Assert.False(result.Quiet);
        Assert.True(result.Status.Busy);
        Assert.True(result.Waited >= TimeSpan.FromMilliseconds(250), $"waited {result.Waited}");
    }

    [Fact]
    public async Task WaitForQuietReturnsImmediatelyWhenQuiet()
    {
        using var sandbox = new EnvSandbox();

        var result = await CreateManager().WaitForQuietAsync(
            InstallationFor(sandbox),
            TimeSpan.FromSeconds(2));

        Assert.True(result.Quiet);
        Assert.True(result.Waited < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AcquireManifestLockCreatesDshCompatibleRecordAndReleases()
    {
        using var sandbox = new EnvSandbox();
        var manager = CreateManager();
        var lockPath = sandbox.ProfilePath("package.json.lock");

        var handle = await manager.AcquireManifestLockAsync(InstallationFor(sandbox), TimeSpan.FromSeconds(2));

        Assert.NotNull(handle);
        Assert.True(File.Exists(lockPath));
        string text;
        using (var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        Assert.Equal(OwnPid.ToString(), text.Trim());
        handle!.Dispose();
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task AcquireManifestLockTimesOutOnLiveHolder()
    {
        using var sandbox = new EnvSandbox();
        WriteLock(sandbox.ProfilePath("package.json.lock"), OwnPid + "\n");

        var handle = await CreateManager().AcquireManifestLockAsync(
            InstallationFor(sandbox),
            TimeSpan.FromMilliseconds(300));

        Assert.Null(handle);
    }

    [Fact]
    public async Task AcquireManifestLockTakesOverExitedHolder()
    {
        using var sandbox = new EnvSandbox();
        WriteLock(sandbox.ProfilePath("package.json.lock"), "999999\n");

        var handle = await CreateManager().AcquireManifestLockAsync(
            InstallationFor(sandbox),
            TimeSpan.FromSeconds(2));

        Assert.NotNull(handle);
        handle!.Dispose();
        Assert.False(File.Exists(sandbox.ProfilePath("package.json.lock")));
    }
}
