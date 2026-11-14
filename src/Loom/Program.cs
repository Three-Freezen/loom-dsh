using Loom.Core.Cli;
using Loom.Core.Composition;
using Loom.Core.Infrastructure;
using Loom.Core.Ipc;

namespace Loom;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var invocation = CliParser.Parse(args);

        if (args.Length == 0 || invocation.Tray)
        {
            return RunTray(invocation, initialUi: null);
        }

        if (invocation.Error is not null)
        {
            ConsoleHelper.Attach();
            Console.Error.WriteLine(invocation.Error);
            return ExitCodes.Failed;
        }

        if (invocation.IsUi)
        {
            if (SingleInstance.IsTrayRunning())
            {
                return RunCli(args, invocation);
            }

            return RunTray(invocation, initialUi: invocation.UiTarget ?? "popup");
        }

        return RunCli(args, invocation);
    }

    private static int RunTray(CliInvocation invocation, string? initialUi)
    {
        var options = BuildOptions(invocation);
        AppServices? services = null;
        InstanceLock? instance = null;
        try
        {
            services = new AppServices(options);
            HelperBootstrapper.Ensure(services.Paths);

            instance = new InstanceLock();
            if (!instance.IsFirstInstance)
            {
                instance.SignalShow();
                return 0;
            }

            var app = new App(services, instance, initialUi);
            return app.Run();
        }
        catch (Exception exception)
        {
            ConsoleHelper.Attach();
            Console.Error.WriteLine("Loom 启动失败：" + exception.Message);
            services?.TryLogException(exception, "Tray startup failed");
            return 1;
        }
        finally
        {
            instance?.Dispose();
            services?.Dispose();
        }
    }

    private static int RunCli(string[] args, CliInvocation invocation)
    {
        var options = BuildOptions(invocation);

        if (invocation.RoutesThroughTray && SingleInstance.IsTrayRunning() && string.IsNullOrWhiteSpace(invocation.DataRoot))
        {
            // Must resolve the same data root as the tray process (installed build -> <install>\Data,
            // otherwise the queue directory would not match and CLI commands could not reach the tray).
            var paths = new LoomPaths(DataRootResolver.Resolve(options.DataRoot).Root);
            var client = new QueueClient(paths);
            var response = client.SubmitAsync(args, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            ConsoleHelper.Attach();
            if (response is null)
            {
                Console.Error.WriteLine("托盘正在运行但没有响应命令队列（E_BUSY），请稍后重试。");
                return ExitCodes.Busy;
            }

            if (!string.IsNullOrEmpty(response.StdOut)) Console.Out.Write(response.StdOut);
            if (!string.IsNullOrEmpty(response.StdErr)) Console.Error.Write(response.StdErr);
            return response.ExitCode;
        }

        ConsoleHelper.Attach();
        AppServices? services = null;
        try
        {
            services = new AppServices(options);
            HelperBootstrapper.Ensure(services.Paths);
            var runner = new CliRunner(services.Loom);
            var code = runner.RunAsync(invocation, Console.Out, Console.Error).GetAwaiter().GetResult();
            Console.Out.Flush();
            Console.Error.Flush();
            return code;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("命令失败：" + exception.Message);
            services?.TryLogException(exception, "CLI failed");
            return ExitCodes.Failed;
        }
        finally
        {
            services?.Dispose();
        }
    }

    private static LoomOptions BuildOptions(CliInvocation invocation) => new()
    {
        DataRoot = invocation.DataRoot,
        InstallDir = invocation.InstallDir,
        DshHome = invocation.DshHome,
        ProfileDir = invocation.ProfileDir,
        LogLevel = invocation.LogLevel,
        UpdateSource = invocation.UpdateSource,
    };
}
