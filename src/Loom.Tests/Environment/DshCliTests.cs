using Loom.Core.Environment;
using Loom.Core.Models;
using Xunit;
using Environment = System.Environment;

namespace Loom.Tests.Env;

public sealed class DshCliTests
{
    private const string EchoScript =
        "@echo off\r\n" +
        "echo args=%*\r\n" +
        "echo secret=[%LOOM_TEST_SECRET%]\r\n" +
        "echo runas=%ELECTRON_RUN_AS_NODE%\r\n" +
        "exit /b 7\r\n";

    private const string SlowScript =
        "@echo off\r\n" +
        "ping -n 6 127.0.0.1 > nul\r\n" +
        "exit /b 0\r\n";

    private static void WriteDshCmd(EnvSandbox sandbox, string script) =>
        sandbox.WriteFile(sandbox.InstallPath(@"resources\runtime\cli\bin\dsh.cmd"), script);

    [Fact]
    public async Task RunsPluginCommandWithArgumentArrayAndScrubbedEnvironment()
    {
        using var sandbox = new EnvSandbox();
        WriteDshCmd(sandbox, EchoScript);
        var previous = Environment.GetEnvironmentVariable("LOOM_TEST_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("LOOM_TEST_SECRET", "hmac-secret-value");
            var cli = new DshCli(new RecordingLogger());

            var result = await cli.RunPluginAsync(
                sandbox.CreateInstallation(),
                new[] { "add", "pkg@1.2.3", "--save-exact" },
                TimeSpan.FromSeconds(30));

            Assert.Equal(7, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("plugin --profile desktop", result.StdOut);
            Assert.Contains("add pkg@1.2.3 --save-exact", result.StdOut);
            Assert.Contains("secret=[]", result.StdOut); // credential-shaped env not forwarded
            Assert.Contains("runas=1", result.StdOut); // ELECTRON_RUN_AS_NODE preserved/set
            Assert.Contains("dsh.cmd", result.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(result.CommandLine));
            Assert.True(result.Duration > TimeSpan.Zero);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOOM_TEST_SECRET", previous);
        }
    }

    [Fact]
    public async Task RedactsCredentialShapedArgumentsInRecordedCommandLine()
    {
        using var sandbox = new EnvSandbox();
        WriteDshCmd(sandbox, EchoScript);
        var cli = new DshCli(new RecordingLogger());

        var result = await cli.RunPluginAsync(
            sandbox.CreateInstallation(),
            new[] { "add", "_authToken=hmac-secret-value" },
            TimeSpan.FromSeconds(30));

        Assert.DoesNotContain("hmac-secret-value", result.CommandLine);
        Assert.Contains("***", result.CommandLine);
    }

    [Fact]
    public async Task TimesOutAndKillsTheChildProcess()
    {
        using var sandbox = new EnvSandbox();
        WriteDshCmd(sandbox, SlowScript);
        var cli = new DshCli(new RecordingLogger());

        var result = await cli.RunPluginAsync(
            sandbox.CreateInstallation(),
            new[] { "view", "pkg", "versions", "--json" },
            TimeSpan.FromMilliseconds(400));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task ReportsErrorWhenNoCliCanBeStarted()
    {
        using var sandbox = new EnvSandbox();
        sandbox.DeleteInstallFile(@"resources\runtime\cli\bin\dsh.cmd");
        var cli = new DshCli(new RecordingLogger());

        var result = await cli.RunPluginAsync(
            sandbox.CreateInstallation(),
            new[] { "list" },
            TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdErr));
    }
}
