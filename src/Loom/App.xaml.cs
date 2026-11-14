using System.Windows;
using System.Windows.Threading;
using Loom.Core.Abstractions;
using Loom.Core.Cli;
using Loom.Core.Infrastructure;
using Loom.Core.Ipc;
using Application = System.Windows.Application;

namespace Loom;

public partial class App : Application
{
    private readonly AppServices _services;
    private readonly InstanceLock _instance;
    private readonly string? _initialUi;
    private readonly CancellationTokenSource _cts = new();
    private readonly UiExceptionBudget _uiExceptionBudget = new();
    private TrayHost? _tray;
    private System.Threading.Timer? _timer;
    private int _requestedExitCode;

    /// <summary>
    /// 启动期已经判定失败、但进程可能继续以托盘存活时记录的退出码（P2-1/P3-1）。
    /// <see cref="Program.RunTray"/> 在 <see cref="Application.Run()"/> 返回 0 时用它。
    /// </summary>
    public int RequestedExitCode => Volatile.Read(ref _requestedExitCode);

    /// <summary>
    /// Required only so the XAML-generated <c>App.Main</c> compiles; the real entry point is
    /// <see cref="Program.Main"/> (StartupObject), which always uses the parameterized constructor.
    /// </summary>
    public App()
        : this(new AppServices(new Core.Composition.LoomOptions()), new InstanceLock(), null)
    {
    }

    public App(AppServices services, InstanceLock instance, string? initialUi)
    {
        _services = services;
        _instance = instance;
        _initialUi = initialUi;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _services.TryLogException(exception, "Unhandled exception");
            }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // P3-R7：ui render 是离屏截图模式，不启动任何会删文件/联网/自动操作的后台副作用。
        var isRender = string.Equals(_initialUi, "render", StringComparison.OrdinalIgnoreCase);

        _tray = new TrayHost(_services);
        _services.UiHandler = new UiRouter(this, _tray);
        _services.ExitRequested += (_, _) => Dispatcher.InvokeAsync(Shutdown);
        _tray.Start(suppressStartupSideEffects: isRender);
        if (!isRender)
        {
            _services.StartQueueServer();
            _timer = new System.Threading.Timer(_ => RunTick(), null, 2500, 15000);
        }

        ThreadPool.RegisterWaitForSingleObject(
            _instance.ShowWaitHandle,
            (_, timedOut) =>
            {
                if (!timedOut) Dispatcher.InvokeAsync(() => _tray?.ShowPopup());
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        if (!string.IsNullOrEmpty(_initialUi))
        {
            try
            {
                ShowTarget(_initialUi);
            }
            catch (Exception exception)
            {
                // A failing initial window must not keep a headless render/UI run alive forever.
                _services.TryLogException(exception, "初始界面打开失败（" + _initialUi + "）");
                // P2-1/P3-1：失败必须体现在退出码上（render 失败返回非 0；非 render 先记录、
                // 进程继续以托盘存活，用户退出时把失败码带回调用方）。
                Interlocked.Exchange(ref _requestedExitCode, ExitCodes.Failed);
                if (!isRender)
                {
                    // P2-6：非交互会话不弹模态框（只写日志），避免阻塞 UI 线程。
                    UiInteraction.ShowWarning(
                        "界面打开失败：\n" + exception.Message
                        + "\n\n详细信息已写入日志（数据目录\\logs\\loom.log）。",
                        "Loom for DSH");
                }
            }
            finally
            {
                if (isRender)
                {
                    // 显式带退出码 Shutdown：Application.Run() 会把它作为进程退出码返回。
                    var exitCode = RequestedExitCode == 0 ? ExitCodes.Success : RequestedExitCode;
                    Dispatcher.BeginInvoke(new Action(() => Shutdown(exitCode)));
                }
            }
        }
        else if (!_services.Loom.Config.FirstRunCompleted)
        {
            _tray.ShowWizard();
        }
    }

    private void RunTick()
    {
        // 定时器回调只负责投递：Tick 内部用 Interlocked 串行化并跳过重叠轮次，
        // 这里不再同步等待，避免长时间占用线程池/UI 线程。
        _ = Task.Run(async () =>
        {
            try
            {
                await _services.Loom.TickAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _services.TryLogException(exception, "Scheduler tick failed");
            }
        });
    }

    public void ShowTarget(string target)
    {
        if (_tray is null) return;
        switch (target.ToLowerInvariant())
        {
            case "settings":
                _tray.ShowSettings();
                break;
            case "wizard":
                _tray.ShowWizard();
                break;
            case "report":
                _tray.ShowReport();
                break;
            case "render":
                _tray.RenderSnapshots();
                break;
            case "popup":
            case "status":
            default:
                _tray.ShowPopup();
                break;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // P2-6：不再静默全吞——每次都按 ERROR 记日志并计数；只有达到阈值时提示一次（仅交互会话）。
        var count = _uiExceptionBudget.Count + 1;
        _services.TryLogException(e.Exception, $"UI thread exception #{count}");
        var shouldPrompt = _uiExceptionBudget.Record();
        // 让托盘继续存活（否则单个界面异常就会结束整个进程）。
        e.Handled = true;

        if (shouldPrompt && UiInteraction.CanShowModal)
        {
            Dispatcher.BeginInvoke(new Action(() => UiInteraction.ShowWarning(
                $"界面层连续出现 {_uiExceptionBudget.Count} 次未处理异常，详细信息已写入日志"
                + "（数据目录\\logs\\loom.log）。\n托盘仍会继续运行。",
                "Loom for DSH")));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _timer?.Dispose();
            _timer = null;
            _cts.Cancel();
        }
        catch
        {
            // best effort
        }

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // best effort
        }

        try
        {
            _services.Dispose();
        }
        catch
        {
            // best effort
        }

        base.OnExit(e);
    }

    /// <summary>Bridges queued <c>ui</c> commands from the CLI to the WPF tray.</summary>
    private sealed class UiRouter : IUiCommandHandler
    {
        private readonly App _app;
        private readonly TrayHost _tray;

        public UiRouter(App app, TrayHost tray)
        {
            _app = app;
            _tray = tray;
        }

        public Task<int> ShowAsync(string target, CancellationToken cancellationToken = default)
        {
            try
            {
                if (_app.Dispatcher.CheckAccess())
                {
                    _app.ShowTarget(target);
                }
                else
                {
                    _app.Dispatcher.Invoke(() => _app.ShowTarget(target));
                }

                return Task.FromResult(0);
            }
            catch
            {
                return Task.FromResult(1);
            }
        }
    }
}
