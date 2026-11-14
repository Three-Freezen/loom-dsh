using Loom.Core.Cli;
using Loom.Core.Infrastructure;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>
/// 托盘运行时的 CLI 路由（P2-9）：写命令在数据根一致时一律排队，读命令可直连。
/// </summary>
public sealed class TrayCommandRoutingTests
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(), "loom-tests", "routing-" + Guid.NewGuid().ToString("N"));

    private static CliInvocation Invocation(string command, string? dataRoot = null) => new()
    {
        Command = command,
        DataRoot = dataRoot,
    };

    [Fact]
    public void TrayNotRunningMeansDirect()
    {
        Assert.Equal(
            TrayCommandRoute.Direct,
            TrayCommandRouting.Decide(Invocation("snapshot"), trayRunning: false, Root));
    }

    [Fact]
    public void WriteCommandWithSameExplicitDataRootIsQueued()
    {
        var invocation = Invocation("snapshot", Root);

        Assert.Equal(TrayCommandRoute.Queue, TrayCommandRouting.Decide(invocation, true, Root));
    }

    [Fact]
    public void SameDataRootComparisonIgnoresTrailingSeparatorAndCase()
    {
        var invocation = Invocation("restore", Root + Path.DirectorySeparatorChar);
        var trayRoot = Root.ToUpperInvariant();

        Assert.Equal(TrayCommandRoute.Queue, TrayCommandRouting.Decide(invocation, true, trayRoot));
    }

    [Fact]
    public void DifferentExplicitDataRootStaysDirect()
    {
        var invocation = Invocation("snapshot", Root + "-other");

        Assert.Equal(TrayCommandRoute.Direct, TrayCommandRouting.Decide(invocation, true, Root));
    }

    [Fact]
    public void ReadCommandsStayDirect()
    {
        foreach (var command in new[] { "status", "report", "doctor" })
        {
            Assert.Equal(
                TrayCommandRoute.Direct,
                TrayCommandRouting.Decide(Invocation(command, Root), true, Root));
        }
    }

    [Fact]
    public void UiAndCheckAreTreatedAsTrayCommands()
    {
        Assert.Equal(TrayCommandRoute.Queue, TrayCommandRouting.Decide(Invocation("ui", Root), true, Root));
        Assert.Equal(TrayCommandRoute.Queue, TrayCommandRouting.Decide(Invocation("check", Root), true, Root));
        Assert.Equal(TrayCommandRoute.Queue, TrayCommandRouting.Decide(Invocation("update", Root), true, Root));
        Assert.Equal(
            TrayCommandRoute.Queue,
            TrayCommandRouting.Decide(Invocation("install-autostart", Root), true, Root));
    }

    [Fact]
    public void InvalidOrHelpInvocationStaysDirect()
    {
        var invalid = Invocation("snapshot", Root);
        invalid.Error = "未知参数";
        Assert.Equal(TrayCommandRoute.Direct, TrayCommandRouting.Decide(invalid, true, Root));

        var help = Invocation("snapshot", Root);
        help.Help = true;
        Assert.Equal(TrayCommandRoute.Direct, TrayCommandRouting.Decide(help, true, Root));
    }

    [Fact]
    public void ClassifiesWriteCommands()
    {
        Assert.True(TrayCommandRouting.IsWriteCommand("snapshot"));
        Assert.True(TrayCommandRouting.IsWriteCommand("RESTORE"));
        Assert.False(TrayCommandRouting.IsWriteCommand("status"));
        Assert.False(TrayCommandRouting.IsWriteCommand("version"));
        Assert.False(TrayCommandRouting.IsWriteCommand(null));
    }

    // ------------------------------------------------------------ P3-R8：只读/dry-run 跨根

    [Fact]
    public void ReadOnlyInvocationsAreIdentified()
    {
        var upgradeDryRun = Invocation("upgrade");
        upgradeDryRun.DryRun = true;
        Assert.True(TrayCommandRouting.IsReadOnlyInvocation(upgradeDryRun));

        var recoveryPlan = new CliInvocation { Command = "recovery", Positionals = { "plan" } };
        Assert.True(TrayCommandRouting.IsReadOnlyInvocation(recoveryPlan));

        var rollbackDryRun = new CliInvocation
        {
            Command = "recovery",
            Positionals = { "rollback", "20260101-000000-recovery-deadbeef" },
            DryRun = true,
        };
        Assert.True(TrayCommandRouting.IsReadOnlyInvocation(rollbackDryRun));

        Assert.False(TrayCommandRouting.IsReadOnlyInvocation(Invocation("upgrade")));
        Assert.False(TrayCommandRouting.IsReadOnlyInvocation(
            new CliInvocation { Command = "recovery", Positionals = { "apply" } }));
        Assert.False(TrayCommandRouting.IsReadOnlyInvocation(Invocation("snapshot")));

        // update 没有 dry-run 实现：带 --dry-run 也仍按写命令处理（不能绕过跨根拒绝）。
        var updateDryRun = Invocation("update");
        updateDryRun.DryRun = true;
        Assert.False(TrayCommandRouting.IsReadOnlyInvocation(updateDryRun));
    }

    [Fact]
    public void DryRunAndReadOnlyCommandsAreNotRejectedForDifferentTrayRoot()
    {
        var handshake = new TrayRootHandshake { Pid = 1, Root = Root + "-tray", Version = "0.2.0" };

        var upgradeDryRun = Invocation("upgrade");
        upgradeDryRun.DryRun = true;
        Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(upgradeDryRun, handshake));

        var recoveryPlan = new CliInvocation { Command = "recovery", Positionals = { "plan" } };
        Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(recoveryPlan, handshake));

        var rollbackDryRun = new CliInvocation
        {
            Command = "recovery",
            Positionals = { "rollback", "20260101-000000-recovery-deadbeef" },
            DryRun = true,
        };
        Assert.False(TrayCommandRouting.ShouldRejectDifferentRoot(rollbackDryRun, handshake));

        // 写命令仍必须拒绝（P2-9 语义不变）。
        Assert.True(TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("upgrade"), handshake));
        Assert.True(TrayCommandRouting.ShouldRejectDifferentRoot(
            new CliInvocation { Command = "recovery", Positionals = { "apply" } }, handshake));
        Assert.True(TrayCommandRouting.ShouldRejectDifferentRoot(Invocation("snapshot"), handshake));
    }
}
