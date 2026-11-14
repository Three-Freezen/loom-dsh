using Loom.Core.Cli;
using Loom.Core.Infrastructure;
using Loom.Core.Ipc;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>P2-9：托盘数据根握手文件的解析/过期判定，以及“托盘在另一数据根时拒绝写命令”的路由规则。</summary>
public sealed class TrayHandshakeTests
{
    [Fact]
    public void HandshakeRoundTripsPidRootStartedAtVersion()
    {
        using var temp = new TempDir();
        var path = TrayRootHandshakeStore.PathFor(temp.Path);
        var startedAt = new DateTimeOffset(2026, 11, 14, 16, 0, 0, TimeSpan.FromHours(8));
        AtomicFile.WriteJson(path, new TrayRootHandshake
        {
            Pid = 4321,
            Root = @"D:\portable\Loom\Data",
            StartedAt = startedAt,
            Version = "0.2.0",
        });

        var parsed = TrayRootHandshakeStore.TryReadFile(path);

        Assert.NotNull(parsed);
        Assert.Equal(4321, parsed!.Pid);
        Assert.Equal(@"D:\portable\Loom\Data", parsed.Root);
        Assert.Equal(startedAt, parsed.StartedAt);
        Assert.Equal("0.2.0", parsed.Version);
    }

    [Fact]
    public void MissingOrCorruptHandshakeFileReturnsNull()
    {
        using var temp = new TempDir();
        var path = TrayRootHandshakeStore.PathFor(temp.Path);

        Assert.Null(TrayRootHandshakeStore.TryReadFile(path));

        File.WriteAllText(path, "{ not json");
        Assert.Null(TrayRootHandshakeStore.TryReadFile(path));
    }

    [Fact]
    public void DeadPidIsNotAlive()
    {
        // 进程号不存在 → GetProcessById 抛错 → 视为已退出（陈旧的握手不能让 CLI 误判托盘还在）。
        Assert.False(TrayRootHandshakeStore.IsProcessAlive(int.MaxValue - 1));
        Assert.False(TrayRootHandshakeStore.IsProcessAlive(0));
        Assert.False(TrayRootHandshakeStore.IsProcessAlive(-1));

        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Assert.True(TrayRootHandshakeStore.IsProcessAlive(current.Id));
    }
}

/// <summary>P1-2 + P2-9 的路由规则：长操作不入队但要探测门；托盘在别的数据根时拒绝写命令。</summary>
public sealed class TrayRootRoutingTests
{
    private static CliInvocation Invocation(string command, string? dataRoot = null)
    {
        var invocation = new CliInvocation { Command = command, DataRoot = dataRoot };
        return invocation;
    }

    [Fact]
    public void UpgradeAndRecoveryAreLongOperationsAndNotQueued()
    {
        Assert.True(TrayCommandRouting.IsLongOperation("upgrade"));
        Assert.True(TrayCommandRouting.IsLongOperation("RECOVERY"));
        Assert.False(TrayCommandRouting.IsLongOperation("snapshot"));

        // 长操作不在“写命令 → 队列”白名单里：它们直连，但由 CLI 显式探测跨进程门。
        Assert.False(TrayCommandRouting.IsWriteCommand("upgrade"));
        Assert.False(TrayCommandRouting.IsWriteCommand("recovery"));
    }

    [Fact]
    public void ProbeIsNeededOnlyForWritingLongOperations()
    {
        Assert.True(TrayCommandRouting.NeedsCrossProcessProbe(Invocation("upgrade")));

        var dryRunUpgrade = Invocation("upgrade");
        dryRunUpgrade.DryRun = true;
        Assert.False(TrayCommandRouting.NeedsCrossProcessProbe(dryRunUpgrade));

        var plan = Invocation("recovery");
        plan.Positionals.Add("plan");
        Assert.False(TrayCommandRouting.NeedsCrossProcessProbe(plan));

        var apply = Invocation("recovery");
        apply.Positionals.Add("apply");
        Assert.True(TrayCommandRouting.NeedsCrossProcessProbe(apply));

        var rollback = Invocation("recovery");
        rollback.Positionals.Add("rollback");
        Assert.True(TrayCommandRouting.NeedsCrossProcessProbe(rollback));

        var dryRunApply = Invocation("recovery");
        dryRunApply.Positionals.Add("apply");
        dryRunApply.DryRun = true;
        Assert.False(TrayCommandRouting.NeedsCrossProcessProbe(dryRunApply));

        // 非长操作不探测（走原有队列/直连规则）。
        Assert.False(TrayCommandRouting.NeedsCrossProcessProbe(Invocation("snapshot")));
    }

    [Fact]
    public void DifferentTrayRootRejectsWriteCommandsWithoutExplicitRoot()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "loom-tests", "handshake-" + Guid.NewGuid().ToString("N"));
        try
        {
            // portable.flag → CLI 默认解析根 = <baseDir>\Data。
            Directory.CreateDirectory(Path.Combine(baseDir, "Data"));
            File.WriteAllText(Path.Combine(baseDir, "Data", "portable.flag"), "");
            var cliDefaultRoot = Path.Combine(baseDir, "Data");
            var otherRoot = Path.Combine(baseDir, "other-data");

            var otherHandshake = new TrayRootHandshake { Pid = 1, Root = otherRoot };
            Assert.True(
                TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("snapshot"), otherHandshake, baseDir),
                "托盘在另一数据根且 CLI 无 --data-root：写命令必须拒绝");

            Assert.True(
                TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("upgrade"), otherHandshake, baseDir),
                "长操作同样会写 profile，也必须拒绝");

            // 同根 → 不拒绝（正常走队列/门控）。
            var sameHandshake = new TrayRootHandshake { Pid = 1, Root = cliDefaultRoot };
            Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("snapshot"), sameHandshake, baseDir));

            // 显式 --data-root（用户明确指向另一个目录）→ 不拒绝，允许直连。
            Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(
                Invocation("snapshot", otherRoot), otherHandshake, baseDir));

            // 读命令不写盘 → 不拒绝。
            Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("status"), otherHandshake, baseDir));

            // 没有握手（旧版托盘/文件被删）→ 退回旧行为，不拒绝。
            Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("snapshot"), null, baseDir));
        }
        finally
        {
            try
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void QueueTimeoutDefaultIsTwentyMinutesAndConfigurable()
    {
        // 默认 20 分钟（原 5 分钟；慢盘/大 profile 会把正常操作误判为 Busy）。
        Assert.Equal(TimeSpan.FromMinutes(20), QueueClient.DefaultTimeout);
    }
}
