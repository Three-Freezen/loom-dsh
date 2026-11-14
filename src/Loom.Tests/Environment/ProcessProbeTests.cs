using System.Diagnostics;
using Loom.Core.Environment;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

public sealed class ProcessProbeTests
{
    [Fact]
    public void IsProcessAliveForCurrentProcess()
    {
        var probe = new ProcessProbe(new RecordingLogger());

        Assert.True(probe.IsProcessAlive(Environment.ProcessId));
    }

    [Fact]
    public void IsProcessAliveForImpossiblePid()
    {
        var probe = new ProcessProbe(new RecordingLogger());

        Assert.False(probe.IsProcessAlive(0));
        Assert.False(probe.IsProcessAlive(-1));
        Assert.False(probe.IsProcessAlive(2147483000));
    }

    [Fact]
    public void HasAnyForNonexistentNameIsFalse()
    {
        var probe = new ProcessProbe(new RecordingLogger());

        Assert.False(probe.HasAny("loom-nonexistent-process-xyz"));
    }

    [Fact]
    public void FindDshProcessesFiltersForeignInstallDirectories()
    {
        var probe = new ProcessProbe(new RecordingLogger());
        var fakeInstall = Path.Combine(Path.GetTempPath(), "loom-env-tests", Guid.NewGuid().ToString("N"));

        var processes = probe.FindDshProcesses(fakeInstall);

        Assert.All(processes, process => Assert.True(process.Pid > 0));
        Assert.All(
            processes,
            process => Assert.True(
                process.ExecutablePath is null || process.ExecutablePath.StartsWith(fakeInstall, StringComparison.OrdinalIgnoreCase),
                $"unexpected foreign process path {process.ExecutablePath}"));
    }

    [Fact]
    public void FindsRealDshProcessesReadOnly()
    {
        if (!RealDsh.IsInstalled) return;
        var probe = new ProcessProbe(new RecordingLogger());

        var processes = probe.FindDshProcesses(RealDsh.InstallDir);

        // The app may not be running; when it is, every returned process must be the
        // installation's own executable (or have an inaccessible path).
        foreach (var process in processes)
        {
            Assert.True(process.Pid > 0);
            Assert.StartsWith("DeepSeek Harness", process.Name, StringComparison.OrdinalIgnoreCase);
            if (process.ExecutablePath is not null)
            {
                Assert.Equal(
                    RealDsh.InstallDir,
                    Path.GetDirectoryName(process.ExecutablePath),
                    ignoreCase: true);
            }
        }
    }

    [Fact]
    public void HasAnyFindsARunningProcessByItsOwnName()
    {
        var probe = new ProcessProbe(new RecordingLogger());
        using var current = Process.GetCurrentProcess();
        var name = current.ProcessName;

        Assert.True(probe.HasAny(name));
    }
}
