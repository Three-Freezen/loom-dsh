using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Update;

/// <summary>
/// In-app update flow for the installed (Inno Setup) build, modeled on DeskOrder's UpdateService:
/// probe GitHub Releases for the newest tag, download the fixed-name setup asset into the user's
/// Downloads folder, then run it silently against the current install directory
/// (<c>/SILENT /DIR=...</c>); Inno's <c>[Run]</c> entry restarts the app after the upgrade.
///
/// Design notes:
/// <list type="bullet">
/// <item>Anonymous GitHub access only — no token is shipped or stored, so the public repo needs no secrets.</item>
/// <item>Portable/dev builds report <see cref="UpdateState.NotSupported"/>; they still can check, but apply is disabled.</item>
/// <item>Auto-check is throttled to once per 24 h (also on failures) so a dead network or a missing
/// release never turns into a request storm.</item>
/// <item>After a successful upgrade the next launch deletes the downloaded installer (opt-in, default on)
/// through <see cref="ConsumePendingSetupCleanup"/>.</item>
/// <item>Test seams: <see cref="SourceOverride"/> (directory containing the fixed-name asset, no network),
/// <see cref="LatestTagOverride"/> and <see cref="DownloadPathOverride"/> — used by the test suite and
/// the sandbox acceptance script.</item>
/// </list>
/// </summary>
public sealed class UpdateService
{
    /// <summary>Public repository displayed in the UI and used for links.</summary>
    public const string RepositoryUrl = "https://github.com/Three-Freezen/loom-dsh";

    /// <summary>Auto-check throttle; a failed check also counts so offline machines do not retry on every start.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    private readonly LoomPaths _paths;
    private readonly Func<LoomConfig> _configAccessor;
    private readonly Action<LoomConfig> _configSaver;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private UpdateState _state = UpdateState.Idle;
    private string? _newVersion;
    private string? _errorText;
    private string? _messageText;
    private int _progressPercent;
    private bool _checking;
    private bool _downloading;

    public UpdateService(
        LoomPaths paths,
        Func<LoomConfig> configAccessor,
        Action<LoomConfig> configSaver,
        ILogger? logger = null)
    {
        _paths = paths;
        _configAccessor = configAccessor;
        _configSaver = configSaver;
        _logger = logger;
    }

    /// <summary>Install-shape facts. Tests may replace it with a temp directory containing <c>unins000.exe</c>.</summary>
    public AppInstallInfo InstallInfo { get; set; } = AppInstallInfo.ForProcess();

    /// <summary>Directory that contains the fixed-name installer (local test source; skips network access).</summary>
    public string? SourceOverride { get; set; }

    /// <summary>Forces the remote tag (test hook; bypasses the GitHub probe).</summary>
    public string? LatestTagOverride { get; set; }

    /// <summary>Overrides the download destination (test hook / <c>--download-dir</c>).</summary>
    public string? DownloadPathOverride { get; set; }

    /// <summary>Raised on every state/progress change. May be raised on a background thread.</summary>
    public event Action? StateChanged;

    public UpdateState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public string? NewVersion
    {
        get
        {
            lock (_gate) return _newVersion;
        }
    }

    public string? ErrorText
    {
        get
        {
            lock (_gate) return _errorText;
        }
    }

    /// <summary>Informational message (e.g. "upstream has no release yet"), never an error.</summary>
    public string? MessageText
    {
        get
        {
            lock (_gate) return _messageText;
        }
    }

    public int ProgressPercent
    {
        get
        {
            lock (_gate) return _progressPercent;
        }
    }

    public string CurrentVersion => LoomPaths.ToolVersion;

    public string ReleaseUrl => GitHubReleaseFeed.ReleasesLatestUrl;

    /// <summary>Installed builds support the silent-upgrade flow; portable/dev builds do not.</summary>
    public bool InAppUpdateSupported => InstallInfo.IsInstalledBuild;

    /// <summary>Where the setup asset is downloaded to (Downloads folder when writable, else <c>&lt;data-root&gt;\updates</c>).</summary>
    public string SetupDownloadPath => DownloadPathOverride ?? ResolveDownloadPath();

    /// <summary>Marker consumed on the first launch of the upgraded version (two lines: setup path, old version).</summary>
    public string PendingSetupCleanupPath => Path.Combine(_paths.DataRoot, "pending-setup-cleanup.txt");

    // ------------------------------------------------------------------ check

    /// <summary>
    /// Checks GitHub Releases for a newer version. Returns the remote version when an update is
    /// available, otherwise null (already newest / no release yet). Never throws for network errors:
    /// failures land in <see cref="ErrorText"/> and <see cref="State"/> = Failed.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_checking) return null;
            _checking = true;
            _state = UpdateState.Checking;
            _errorText = null;
            _messageText = null;
        }

        RaiseStateChanged();
        try
        {
            var tag = LatestTagOverride;
            if (string.IsNullOrWhiteSpace(tag)) tag = ResolveLocalTag();

            if (string.IsNullOrWhiteSpace(tag))
            {
                tag = await GitHubReleaseFeed.ResolveLatestTagAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                lock (_gate)
                {
                    _state = UpdateState.UpToDate;
                    _messageText = "上游暂无发布版本（首次发布前属正常）。";
                }

                TouchLastCheckTime();
                RaiseStateChanged();
                return null;
            }

            var newer = GitHubReleaseFeed.ParseNewerVersion(tag, CurrentVersion);
            lock (_gate)
            {
                _newVersion = newer;
                _state = newer is null ? UpdateState.UpToDate : UpdateState.Available;
                _messageText = newer is null ? $"已是最新版本（上游 {tag}）。" : null;
            }

            TouchLastCheckTime();
            _logger?.Log(LogLevel.Info, newer is null
                ? $"Update: already latest (remote {tag}, current {CurrentVersion})"
                : $"Update: {newer} available (remote {tag}, current {CurrentVersion})");
            RaiseStateChanged();
            return newer is null
                ? null
                : new UpdateCheckResult(newer, ReleaseUrl, GitHubReleaseFeed.AssetUrl);
        }
        catch (NoReleaseException)
        {
            TouchLastCheckTime();
            lock (_gate)
            {
                _state = UpdateState.UpToDate;
                _messageText = "上游暂无发布版本（首次发布前属正常）。";
            }

            RaiseStateChanged();
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TouchLastCheckTime();
            lock (_gate)
            {
                _state = UpdateState.Failed;
                _newVersion = null;
                _errorText = FriendlyError(exception);
            }

            _logger?.LogException(exception, "Update check failed");
            RaiseStateChanged();
            return null;
        }
        finally
        {
            lock (_gate) _checking = false;
        }
    }

    /// <summary>
    /// Background check used at startup: skipped when the build does not support in-app updates, when
    /// the user disabled auto-check, or when the last check was less than <see cref="AutoCheckInterval"/> ago.
    /// </summary>
    public async Task<UpdateCheckResult?> AutoCheckIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (!InAppUpdateSupported) return null;

        var config = _configAccessor();
        if (!config.AutoCheckUpdate) return null;
        if (config.LastUpdateCheckUtc != default
            && DateTimeOffset.UtcNow - config.LastUpdateCheckUtc < AutoCheckInterval)
        {
            return null;
        }

        return await CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ download

    /// <summary>Downloads the setup asset for the version found by <see cref="CheckAsync"/>.</summary>
    public async Task<bool> DownloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_downloading) return false;
            if (_state != UpdateState.Available || string.IsNullOrEmpty(_newVersion))
            {
                _errorText = "没有可下载的新版本，请先检查更新。";
                RaiseStateChangedLocked();
                return false;
            }

            _downloading = true;
            _state = UpdateState.Downloading;
            _progressPercent = 0;
            _errorText = null;
            _messageText = null;
        }

        RaiseStateChanged();
        var target = SetupDownloadPath;
        var progress = new Progress<int>(percent =>
        {
            lock (_gate) _progressPercent = Math.Clamp(percent, 0, 100);
            RaiseStateChanged();
        });
        IProgress<int> progressSink = progress;

        try
        {
            var localAsset = SourceOverride is { Length: > 0 } directory
                ? Path.Combine(directory, GitHubReleaseFeed.AssetName)
                : null;

            if (localAsset is not null && File.Exists(localAsset))
            {
                var localLength = new FileInfo(localAsset).Length;
                if (localLength < GitHubReleaseFeed.MinimumAssetBytes)
                {
                    throw new InvalidOperationException("本地更新源里的安装包体积过小，不是有效安装包。");
                }

                var assetDirectory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(assetDirectory)) Directory.CreateDirectory(assetDirectory);
                File.Copy(localAsset, target, overwrite: true);
                progressSink.Report(100);
            }
            else
            {
                await GitHubReleaseFeed
                    .DownloadToFileAsync(GitHubReleaseFeed.AssetUrl, target, progressSink, cancellationToken)
                    .ConfigureAwait(false);
            }

            var length = File.Exists(target) ? new FileInfo(target).Length : 0;
            if (length < GitHubReleaseFeed.MinimumAssetBytes)
            {
                throw new InvalidOperationException("下载的安装包不完整（体积过小），未标记为可更新。");
            }

            WritePendingSetupCleanup(target);
            lock (_gate)
            {
                _state = UpdateState.Ready;
                _progressPercent = 100;
            }

            _logger?.Log(LogLevel.Info, $"Update: setup downloaded to {target} ({length} bytes)");
            RaiseStateChanged();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                _state = UpdateState.Failed;
                _errorText = FriendlyError(exception);
            }

            _logger?.LogException(exception, "Update download failed");
            RaiseStateChanged();
            return false;
        }
        finally
        {
            lock (_gate) _downloading = false;
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Launches the downloaded installer silently against the current install directory. The caller
    /// must shut the current process down afterwards so Inno's <c>RestartApplications</c>/<c>[Run]</c>
    /// entry can replace the files; the setup restarts Loom when the silent upgrade finishes.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (!InAppUpdateSupported)
        {
            throw new InvalidOperationException(
                "当前为免安装/开发版，应用内更新不可用；请到 GitHub Releases 手动下载最新安装包。");
        }

        var setupPath = SetupDownloadPath;
        if (!File.Exists(setupPath))
        {
            throw new InvalidOperationException("安装包尚未下载完成，请先下载后再重启更新。");
        }

        var installDir = InstallInfo.InstallDir;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new InvalidOperationException("无法确定当前安装目录，未启动安装程序。");
        }

        var arguments = AppInstallInfo.BuildSilentUpgradeArguments(installDir);
        _logger?.Log(LogLevel.Info, $"Update: launching installer \"{setupPath}\" {arguments}");
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    // ------------------------------------------------------------------ post-upgrade cleanup

    /// <summary>
    /// First launch of an upgraded version consumes the pending-cleanup marker: when the marker points
    /// at our fixed asset name and the current version is newer than the recorded old version (i.e. the
    /// upgrade really happened), the downloaded installer is deleted. Invalid/stale markers are simply
    /// discarded; a locked installer keeps the marker for the next launch (never delete on failure).
    /// </summary>
    public void ConsumePendingSetupCleanup()
    {
        try
        {
            var marker = PendingSetupCleanupPath;
            if (!File.Exists(marker)) return;

            var lines = File.ReadAllLines(marker);
            var setupPath = lines.Length > 0 ? lines[0].Trim() : "";
            var oldVersionText = (lines.Length > 1 ? lines[1].Trim() : "").Split('+')[0].TrimStart('v', 'V');

            if (Path.GetFileName(setupPath) != AppInstallInfo.SetupAssetName
                || !SemVersion.TryParse(oldVersionText, out var oldVersion)
                || !SemVersion.TryParse(CurrentVersion.Split('+')[0], out var currentVersion)
                || currentVersion <= oldVersion
                || !File.Exists(setupPath))
            {
                File.Delete(marker);
                return;
            }

            try
            {
                File.Delete(setupPath);
                File.Delete(marker);
                _logger?.Log(LogLevel.Info, $"Update: removed downloaded setup after upgrade ({setupPath})");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Installer may still be running (Inno's bootstrap waits for the whole install to finish).
                // Keep the marker; the next launch retries.
                _logger?.Log(LogLevel.Warn, $"Update: setup is still locked, cleanup deferred ({setupPath})");
            }
        }
        catch
        {
            // Cleanup is best effort: never let it break startup.
        }
    }

    // ------------------------------------------------------------------ helpers

    private string? ResolveLocalTag()
    {
        if (SourceOverride is not { Length: > 0 } directory) return null;
        var asset = Path.Combine(directory, GitHubReleaseFeed.AssetName);
        if (!File.Exists(asset)) return null;
        try
        {
            return FileVersionInfo.GetVersionInfo(asset).ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    private void TouchLastCheckTime()
    {
        try
        {
            var config = _configAccessor();
            config.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _configSaver(config);
        }
        catch (Exception exception)
        {
            // Losing the throttle timestamp only costs one extra probe later.
            _logger?.Log(LogLevel.Warn, "Update: could not persist last-check time: " + exception.Message);
        }
    }

    private string ResolveDownloadPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var downloads = GetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
                if (!string.IsNullOrEmpty(downloads) && IsWritable(downloads))
                {
                    return Path.Combine(downloads, GitHubReleaseFeed.AssetName);
                }
            }
            catch
            {
                // fall through to the data root
            }
        }

        return Path.Combine(_paths.DataRoot, "updates", GitHubReleaseFeed.AssetName);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".loom-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string? GetKnownFolderPath(Guid guid)
    {
        var result = SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out var pointer);
        if (result != 0 || pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private void WritePendingSetupCleanup(string setupPath)
    {
        try
        {
            if (!_configAccessor().DeleteSetupAfterUpdate) return;
            File.WriteAllText(
                PendingSetupCleanupPath,
                setupPath + System.Environment.NewLine + CurrentVersion.Split('+')[0] + System.Environment.NewLine);
        }
        catch (Exception exception)
        {
            // Cleanup is optional; never fail the update because the marker could not be written.
            _logger?.Log(LogLevel.Warn, "Update: could not write pending-cleanup marker: " + exception.Message);
        }
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        NoReleaseException => "上游暂无发布版本。",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "GitHub 访问被限流，请稍后再试。",
        HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } =>
            "代理需要认证，无法检查更新。",
        HttpRequestException { StatusCode: { } status } when (int)status >= 400 =>
            $"GitHub 返回 HTTP {(int)status}。",
        HttpRequestException => "无法连接 GitHub（请检查网络或代理设置）。",
        TaskCanceledException => "检查更新超时，请稍后再试。",
        _ => exception.Message,
    };

    private void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>Raises the event while already holding <see cref="_gate"/> (used before the async work starts).</summary>
    private void RaiseStateChangedLocked() => StateChanged?.Invoke();
}
