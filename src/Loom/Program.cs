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
        var handshakeWritten = false;
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

            // P2-9：托盘启动即写数据根握手（固定写在默认数据根，CLI 不需要知道托盘参数），
            // 退出/崩溃后残留的握手由 pid 存活校验识别为过期。
            TrayRootHandshakeStore.Write(services.Paths.DataRoot);
            handshakeWritten = true;

            var app = new App(services, instance, initialUi);
            var code = app.Run();
            // P2-1：ui render 失败等“启动期已判定失败”的情况需要把非 0 退出码带回给调用方。
            return code != 0 ? code : app.RequestedExitCode;
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
            // 先删握手再释放服务：两者都是 best effort，顺序只影响诊断信息。
            if (handshakeWritten) TrayRootHandshakeStore.Delete();
            instance?.Dispose();
            services?.Dispose();
        }
    }

    private static int RunCli(string[] args, CliInvocation invocation)
    {
        var options = BuildOptions(invocation);
        AppServices? services = null;
        OperationGate? preAcquiredGate = null;
        IDisposable? gateLease = null;
        try
        {
            if (invocation.RoutesThroughTray && SingleInstance.IsTrayRunning())
            {
                // P2-9：托盘真实数据根来自握手文件；旧版托盘没有握手时退回“默认根”假设。
                var handshake = TrayRootHandshakeStore.TryReadLive();
                var trayDataRoot = handshake?.Root ?? DataRootResolver.Resolve(null).Root;
                var cliRoot = DataRootResolver.Resolve(invocation.DataRoot).Root;
                var sameRoot = DataRootResolution.PathEquals(cliRoot, trayDataRoot);

                if (TrayCommandRouting.ShouldRejectDifferentRoot(invocation, handshake))
                {
                    ConsoleHelper.Attach();
                    Console.Error.WriteLine(
                        "托盘正在使用其他数据目录：" + trayDataRoot + "（当前命令会写入 "
                        + cliRoot + "）。");
                    Console.Error.WriteLine(
                        "请退出托盘后重试，或用相同的 --data-root 重新运行（也可以直接从托盘界面发起操作）。");
                    return ExitCodes.NeedsManual;
                }

                // P1-2：upgrade/recovery 这类长操作不入队，但直连前必须先非阻塞探测跨进程门：
                // 拿不到 → Busy（绝不与托盘静默并发）；拿到 → 一直持有到命令结束。
                if (sameRoot && TrayCommandRouting.NeedsCrossProcessProbe(invocation))
                {
                    // P2-R2：门的名字和锁文件都由数据根派生；sameRoot 已确认 CLI 与托盘同一根。
                    preAcquiredGate = new OperationGate(cliRoot);
                    gateLease = preAcquiredGate.TryAcquireCrossProcessLease(TimeSpan.Zero);
                    if (gateLease is null)
                    {
                        ConsoleHelper.Attach();
                        Console.Error.WriteLine(
                            "托盘正在执行其他操作，请稍后再试或从托盘界面发起。");
                        return ExitCodes.Busy;
                    }
                }

                // 托盘与 CLI 必须解析到同一个数据根才会命中同一队列。写命令一律排队（即使显式传了
                // --data-root），读命令直连；判定逻辑见 TrayCommandRouting（有单测）。
                if (sameRoot && TrayCommandRouting.Decide(invocation, trayRunning: true, trayDataRoot)
                    == TrayCommandRoute.Queue)
                {
                    var paths = new LoomPaths(trayDataRoot);
                    var client = new QueueClient(paths);
                    var response = client.SubmitAsync(args, QueueClient.DefaultTimeout).GetAwaiter().GetResult();
                    ConsoleHelper.Attach();
                    if (response is null)
                    {
                        Console.Error.WriteLine(
                            "托盘正在运行但没有响应命令队列（E_BUSY），请稍后重试。");
                        return ExitCodes.Busy;
                    }

                    if (!string.IsNullOrEmpty(response.StdOut)) Console.Out.Write(response.StdOut);
                    if (!string.IsNullOrEmpty(response.StdErr)) Console.Error.Write(response.StdErr);
                    return response.ExitCode;
                }
            }

            ConsoleHelper.Attach();
            // 长操作把预占的跨进程门交给服务：服务内部的 AcquireAsync 只做进程内串行化，
            // 不会重复获取命名互斥量（避免自锁），门由本方法在命令结束时释放。
            services = new AppServices(options, preAcquiredGate);
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
            gateLease?.Dispose();
            preAcquiredGate?.Dispose();
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
