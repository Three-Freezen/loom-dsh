using System.Windows.Input;
using Loom.Core.Abstractions;
using Loom.Services;

namespace Loom.ViewModels;

/// <summary>View model for the report window + manual/failure entry points.</summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly ILoomService _loom;
    private string? _currentPath;
    private string _markdown = "";
    private string _pathText = "";
    private bool _hasReport;
    private bool _isBusy;
    private string _statusText = "";
    private bool _statusIsError;

    public ReportViewModel(ILoomService loom)
    {
        _loom = loom;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(_currentPath), () => !IsBusy);
        CopyPathCommand = new RelayCommand(CopyPath, () => PathText.Length > 0);
        OpenExternalCommand = new RelayCommand(OpenExternal, () => _currentPath is { Length: > 0 });
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !IsBusy);
        RollbackCommand = new RelayCommand(
            () => RollbackRequested?.Invoke(this, EventArgs.Empty),
            () => !IsBusy);
        OpenPluginManagerCommand = new RelayCommand(OpenPluginManager);
        OpenReportsFolderCommand = new RelayCommand(() => ShellLauncher.OpenFolder(_loom.Paths.ReportsDir));
    }

    public event EventHandler? RollbackRequested;

    public ICommand RefreshCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand OpenExternalCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand OpenPluginManagerCommand { get; }
    public ICommand OpenReportsFolderCommand { get; }

    public string Markdown { get => _markdown; private set => Set(ref _markdown, value); }
    public string PathText { get => _pathText; private set { Set(ref _pathText, value); OnPropertyChanged(nameof(HasPath)); } }
    public bool HasPath => PathText.Length > 0;
    public bool HasReport
    {
        get => _hasReport;
        private set
        {
            Set(ref _hasReport, value);
            OnPropertyChanged(nameof(HasNoReport));
        }
    }

    public bool HasNoReport => !HasReport;
    public string EmptyText => UiStrings.ReportEmpty;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string StatusText { get => _statusText; private set { Set(ref _statusText, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public async Task LoadAsync(string? path)
    {
        IsBusy = true;
        StatusText = "";
        try
        {
            var report = string.IsNullOrWhiteSpace(path)
                ? await _loom.GetLatestReportAsync().ConfigureAwait(true)
                : await _loom.GetReportAsync(path!).ConfigureAwait(true);

            _currentPath = report?.MarkdownPath ?? report?.JsonPath ?? path;
            PathText = _currentPath ?? "";
            if (report is not null && !string.IsNullOrWhiteSpace(report.Markdown))
            {
                Markdown = report.Markdown;
                HasReport = true;
            }
            else if (report is not null && !string.IsNullOrWhiteSpace(report.Json))
            {
                Markdown = report.Json;
                HasReport = true;
            }
            else
            {
                Markdown = "";
                HasReport = false;
            }
        }
        catch (Exception exception)
        {
            StatusText = "读取报告失败：" + exception.Message;
            StatusIsError = true;
            HasReport = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyPath()
    {
        if (ShellLauncher.CopyToClipboard(PathText))
        {
            StatusText = UiStrings.ReportCopied;
            StatusIsError = false;
        }
    }

    private void OpenExternal()
    {
        if (!string.IsNullOrWhiteSpace(_currentPath)) ShellLauncher.Open(_currentPath);
    }

    private async Task RetryAsync()
    {
        IsBusy = true;
        StatusText = "正在重新检查兼容性…";
        try
        {
            var outcome = await _loom.RunCheckAsync(new CheckRequest()).ConfigureAwait(true);
            StatusText = string.IsNullOrWhiteSpace(outcome.Message) ? "检查已完成" : outcome.Message;
            StatusIsError = !outcome.Success;
            await LoadAsync(outcome.Report?.MarkdownPath ?? _currentPath).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = "检查失败：" + exception.Message;
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenPluginManager()
    {
        var profile = _loom.GetStatus().Installation?.ProfileDir;
        if (string.IsNullOrWhiteSpace(profile)) return;
        var pluginManager = System.IO.Path.Combine(profile, Loom.Core.Infrastructure.DshDefaults.PluginManagerDir);
        ShellLauncher.OpenFolder(System.IO.Directory.Exists(pluginManager) ? pluginManager : profile);
    }
}
