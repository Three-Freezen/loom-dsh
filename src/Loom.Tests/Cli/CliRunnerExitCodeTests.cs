using Loom.Core.Cli;
using Loom.Core.Infrastructure;
using Xunit;

namespace Loom.Tests.Cli;

/// <summary>
/// CLI 参数错误退出码（P2-7）：直接 CLI 与托盘队列执行器都必须是 Failed(1)，不能误报 Busy(4)。
/// 这些分支在访问 ILoomService 之前返回，因此可以传 null 服务。
/// </summary>
public sealed class CliRunnerExitCodeTests
{
    private static async Task<int> RunAsync(params string[] args)
    {
        var invocation = CliParser.Parse(args);
        var runner = new CliRunner(null!);
        return await runner.RunAsync(invocation, new StringWriter(), new StringWriter());
    }

    [Fact]
    public async Task UnknownOptionReturnsFailedNotBusy()
    {
        var code = await RunAsync("status", "--definitely-not-an-option");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.NotEqual(ExitCodes.Busy, code);
    }

    [Fact]
    public async Task UnknownCommandReturnsFailedNotBusy()
    {
        var code = await RunAsync("frobnicate");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.NotEqual(ExitCodes.Busy, code);
    }

    [Fact]
    public async Task ConflictingUpdateFlagsReturnFailedNotBusy()
    {
        var code = await RunAsync("update", "--download", "--apply");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.NotEqual(ExitCodes.Busy, code);
    }
}
