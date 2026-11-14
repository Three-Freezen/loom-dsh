using Loom.Core.Environment;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

public sealed class AutostartServiceTests
{
    [Fact]
    public void BuildCommandQuotesFullExecutablePathAndAddsTrayArgument()
    {
        var command = AutostartService.BuildCommand(@"C:\Program Files\Loom for DSH\loom-dsh.exe");

        Assert.Equal(@"""C:\Program Files\Loom for DSH\loom-dsh.exe"" --tray", command);
    }

    [Fact]
    public void BuildCommandToleratesAnAlreadyQuotedPath()
    {
        var command = AutostartService.BuildCommand(@"""C:\Loom\loom-dsh.exe""");

        Assert.Equal(@"""C:\Loom\loom-dsh.exe"" --tray", command);
    }

    [Fact]
    public void NormalizeCommandQuotesBareExecutableWithSpaces()
    {
        var normalized = AutostartService.NormalizeCommand(@"C:\Program Files\Loom\loom-dsh.exe --tray");

        Assert.Equal(@"""C:\Program Files\Loom\loom-dsh.exe"" --tray", normalized);
    }

    [Fact]
    public void NormalizeCommandLeavesQuotedCommandUntouched()
    {
        var source = @"""C:\Loom\loom-dsh.exe"" --tray";

        Assert.Equal(source, AutostartService.NormalizeCommand(source));
    }

    [Fact]
    public void NormalizeCommandQuotesSingleToken()
    {
        Assert.Equal(@"""C:\Loom\loom-dsh.exe""", AutostartService.NormalizeCommand(@"C:\Loom\loom-dsh.exe"));
    }

    [Fact]
    public void RegistryReadsNeverThrow()
    {
        var service = new AutostartService(new RecordingLogger());

        var enabled = service.IsEnabled();
        var command = service.GetRegisteredCommand();

        Assert.Equal(enabled, command is { Length: > 0 });
        if (command is { Length: > 0 })
        {
            Assert.StartsWith("\"", command);
        }
    }
}
