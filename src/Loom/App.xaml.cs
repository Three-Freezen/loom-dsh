using System.Windows;
using System.Windows.Threading;
using Loom.Core.Abstractions;
using Loom.Core.Ipc;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Loom;

public partial class App : Application
{
    private readonly AppServices _services;
    private readonly InstanceLock _instance;
    private readonly string? _initialUi;
    private readonly CancellationTokenSource _cts = new();
    private TrayHost? _tray;
    private System.Threading.Timer? _timer;

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

        _tray = new TrayHost(_services);
        _services.UiHandler = new UiRouter(this, _tray);
        _services.ExitRequested += (_, _) => Dispatcher.InvokeAsync(Shutdown);
        _tray.Start();
        _services.StartQueueServer();

        _timer = new System.Threading.Timer(_ => RunTick(), null, 2500, 15000);

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
            var isRender = string.Equals(_initialUi, "render", StringComparison.OrdinalIgnoreCase);
            try
            {
                ShowTarget(_initialUi);
            }
            catch (Exception exception)
            {
                // A failing initial window must not keep a headless render/UI run alive forever.
                _services.TryLogException(exception, "初始界面打开失败（" + _initialUi + "）");
                if (!isRender)
                {
                    MessageBox.Show(
                        "界面打开失败：\n" + exception.Message,
                        "Loom for DSH",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                if (isRender)
                {
                    Dispatcher.BeginInvoke(new Action(Shutdown));
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
        try
        {
            _services.Loom.TickAsync(_cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _services.TryLogException(exception, "Scheduler tick failed");
        }
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
        _services.TryLogException(e.Exception, "UI thread exception");
        e.Handled = true;
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
