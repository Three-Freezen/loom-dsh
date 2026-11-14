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
    private readonly Action<Action<LoomConfig>>? _configMutator;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly object _downloadPathGate = new();

    private UpdateState _state = UpdateState.Idle;
    private string? _newVersion;
    private string? _errorText;
    private string? _messageText;
    private int _progressPercent;
    private bool _checking;
    private bool _downloading;
    private bool _integrityVerified;
    private string? _integrityText;

    /// <summary>P3-R9：Downloads 目录解析/可写探测只做一次，之后复用（getter 不再反复建删探针）。</summary>
    private string? _cachedResolvedDownloadPath;
    private bool _downloadPathResolved;

    /// <param name="paths">数据目录。</param>
    /// <param name="configAccessor">读取当前配置（后台字段写回需要最新引用）。</param>
    /// <param name="configSaver">保存配置（旧路径；有 <paramref name="configMutator"/> 时优先用它）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="configMutator">
    /// 配置“锁内读最新 → 改 → 保存”（P2-4）。为 null 时退回 <paramref name="configSaver"/> 的整份写盘。
    /// </param>
    public UpdateService(
        LoomPaths paths,
        Func<LoomConfig> configAccessor,
        Action<LoomConfig> configSaver,
        ILogger? logger = null,
        Action<Action<LoomConfig>>? configMutator = null)
    {
        _paths = paths;
        _configAccessor = configAccessor;
        _configSaver = configSaver;
        _configMutator = configMutator;
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
    public string SetupDownloadPath => DownloadPathOverride ?? ResolveDownloadPathCached();

    /// <summary>
    /// P2-R1 旁车文件：<c>&lt;data-root&gt;\updates\&lt;asset&gt;.meta.json</c>。
    /// 下载成功时写入；<see cref="ApplyAndRestart"/> 执行前必须存在且与安装包一致。
    /// </summary>
    public string VerifiedSetupMetadataPath => VerifiedSetupMetadataPathFor(_paths, SetupDownloadPath);

    /// <summary>测试缝：读取安装包内嵌 ProductVersion（默认 <see cref="FileVersionInfo"/>）。</summary>
    internal Func<string, string?> SetupVersionReader { get; set; } = ReadSetupProductVersion;

    /// <summary>测试缝：启动安装程序（默认 <see cref="Process.Start(ProcessStartInfo)"/>；测试只记录、不执行）。</summary>
    internal Action<string, string> InstallerLauncher { get; set; } = LaunchInstaller;

    /// <summary>测试缝：解析系统 Downloads 目录，便于断言 getter 只探测一次（P3-R9）。</summary>
    internal Func<string?> DownloadsPathProvider { get; set; } = DefaultDownloadsPath;

    /// <summary>安装包是否通过 SHA256SUMS.txt 校验（P2-3；未取到清单时为 false）。</summary>
    public bool IntegrityVerified
    {
        get
        {
            lock (_gate) return _integrityVerified;
        }
    }

    /// <summary>校验状态的可读文本（“已校验 SHA256” / “未校验（未取到 SHA256SUMS.txt）”）。</summary>
    public string? IntegrityText
    {
        get
        {
            lock (_gate) return _integrityText;
        }
    }

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
        string version;
        lock (_gate)
        {
            // 每次下载都重置校验状态，避免上一轮的“已校验”误导本轮失败的安装包。
            _integrityVerified = false;
            _integrityText = null;
            version = _newVersion ?? "";
        }

        // 旧的旁车先删：本轮下载失败时 apply 必须拒绝，而不是拿上一轮的校验信息执行新文件。
        // （放在 try 内：路径异常也要走统一的 Failed 状态。）
        var progress = new Progress<int>(percent =>
        {
            lock (_gate) _progressPercent = Math.Clamp(percent, 0, 100);
            RaiseStateChanged();
        });
        IProgress<int> progressSink = progress;

        try
        {
            TryDelete(VerifiedSetupMetadataPath);
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
                // 与网络分支保持一致：先写 .part，校验通过后再原子改名，避免半写文件被当成安装包。
                var partPath = target + ".part";
                try
                {
                    File.Copy(localAsset, partPath, overwrite: true);
                    File.Move(partPath, target, overwrite: true);
                }
                catch
                {
                    TryDelete(partPath);
                    throw;
                }

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

            var actualSha256 = await VerifyIntegrityAsync(
                    target,
                    localAsset is null ? null : Path.GetDirectoryName(localAsset),
                    cancellationToken)
                .ConfigureAwait(false);

            // P2-R1：无论清单是否可用，都记录真实文件哈希 + 版本 + 大小 + 时间；跨进程 apply 以此为准。
            WriteVerifiedSetupMetadata(target, actualSha256, length, version);

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
                _integrityVerified = false;
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

    /// <summary>
    /// P2-3：下载完成后校验安装包 SHA256，并返回安装包真实哈希（供旁车记录）。
    /// <list type="bullet">
    ///   <item>本地源：只读本地目录里的 SHA256SUMS.txt（不联网，测试/离线场景依赖它）；</item>
    ///   <item>网络源：尝试 <c>releases/latest/download/SHA256SUMS.txt</c>；</item>
    ///   <item>清单缺失/无对应条目 → 记 WARN、标记“未与发布清单比对”，但仍记录下载哈希（离线/网关场景）；</item>
    ///   <item>清单里有条目但哈希不一致 → 删除安装包并抛错（绝不标记 Ready、绝不执行）。</item>
    /// </list>
    /// </summary>
    private async Task<string> VerifyIntegrityAsync(string setupPath, string? localDirectory, CancellationToken cancellationToken)
    {
        string? manifest = null;
        if (localDirectory is not null)
        {
            var localSums = Path.Combine(localDirectory, GitHubReleaseFeed.ChecksumsAssetName);
            if (File.Exists(localSums))
            {
                manifest = await File.ReadAllTextAsync(localSums, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            manifest = await GitHubReleaseFeed.TryDownloadChecksumsAsync(cancellationToken).ConfigureAwait(false);
        }

        // 真实哈希总是要算：即使拿不到发布清单，也要把“下载到的这个文件”绑定进旁车。
        var actual = ChecksumManifest.ComputeFileSha256(setupPath);
        var expected = ChecksumManifest.FindSha256(manifest, Path.GetFileName(setupPath));
        if (expected is null)
        {
            lock (_gate)
            {
                _integrityVerified = false;
                _integrityText = "未校验（未取到 SHA256SUMS.txt；已记录下载哈希）";
            }

            _logger?.Log(
                LogLevel.Warn,
                "Update: SHA256SUMS.txt 不可用或未包含安装包条目，安装包未与发布清单比对（离线/网关场景）。");
            return actual;
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(setupPath);
            TryDelete(setupPath + ".part");
            _logger?.Log(
                LogLevel.Error,
                $"Update: 安装包 SHA256 校验失败（期望 {ChecksumManifest.Short(expected)}，"
                + $"实际 {ChecksumManifest.Short(actual)}），已删除下载文件。");
            throw new InvalidOperationException(
                $"安装包 SHA256 校验失败（期望 {ChecksumManifest.Short(expected)}，"
                + $"实际 {ChecksumManifest.Short(actual)}），已删除下载文件；请重新下载。");
        }

        lock (_gate)
        {
            _integrityVerified = true;
            _integrityText = "已校验 SHA256";
        }

        _logger?.Log(LogLevel.Info, $"Update: setup SHA256 校验通过（{ChecksumManifest.Short(actual)}）");
        return actual;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// 启动已下载的安装包（静默升级）。调用方随后必须退出，好让 Inno 的
    /// <c>RestartApplications</c>/<c>[Run]</c> 替换文件并重启 Loom。
    ///
    /// P2-R1 / P3-R15：这里的“执行前再复验”由**两段事实**组成，且不再存在“未校验也执行”的分支：
    /// <list type="number">
    ///   <item>同一进程内“下载后立即 apply”：下载时写入数据根的旁车仍在，按旁车复验；</item>
    ///   <item>跨进程（退出托盘后再运行 <c>loom-dsh update --apply</c>）：下载进程的内存哈希已不存在，
    ///         唯一依据是下载成功时写入的 <c>&lt;data-root&gt;\updates\&lt;asset&gt;.meta.json</c>
    ///         （sha256 + version + size + downloadedAt）。缺旁车、哈希/大小不符、安装包内嵌
    ///         ProductVersion 与旁车 version 不一致 → 一律拒绝执行并提示重新下载。</item>
    /// </list>
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

        var metadataPath = VerifiedSetupMetadataPath;
        var metadata = ReadVerifiedSetupMetadata(metadataPath, setupPath, out var metadataError);
        if (metadata is null)
        {
            _logger?.Log(LogLevel.Warn, "Update: 安装包旁车校验缺失或无效，已拒绝执行：" + metadataError);
            throw new InvalidOperationException(metadataError + " 可运行 loom-dsh update --download 重新下载。");
        }

        long actualSize;
        try
        {
            actualSize = new FileInfo(setupPath).Length;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("无法读取安装包大小，已拒绝执行：" + exception.Message);
        }

        if (metadata.Size > 0 && actualSize != metadata.Size)
        {
            throw new InvalidOperationException(
                $"安装包大小与下载记录不一致（记录 {metadata.Size} 字节，实际 {actualSize} 字节），"
                + "已拒绝执行；请重新下载。");
        }

        var actualHash = ChecksumManifest.ComputeFileSha256(setupPath);
        if (!string.Equals(actualHash, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Log(
                LogLevel.Warn,
                $"Update: 安装包 SHA256 与旁车不一致（记录 {ChecksumManifest.Short(metadata.Sha256!)}，"
                + $"实际 {ChecksumManifest.Short(actualHash)}），已拒绝执行。");
            throw new InvalidOperationException(
                "安装包在下载后被修改（SHA256 与下载记录不一致），已拒绝执行；请重新下载。");
        }

        // 版本绑定：安装包内嵌 ProductVersion 必须等于下载时记录的版本，防止拿 A 版本的旁车执行 B 版本。
        // P3：Inno 把 AppVersion 写进 ProductVersion 时会补填充空格（如 "0.2.0      "）；比较本身按
        // 规范化后的 semver 走，这里展示给用户的版本号也必须规范化，避免句子中间拖出一段空白。
        var embeddedVersion = SetupVersionReader(setupPath);
        if (!SameVersion(embeddedVersion, metadata.Version))
        {
            throw new InvalidOperationException(
                $"安装包版本（{DisplayVersion(embeddedVersion)}）与下载记录（{DisplayVersion(metadata.Version)}）不一致，"
                + "已拒绝执行；请重新下载。");
        }

        if (!SemVersion.TryParse(CurrentVersion.Split('+')[0], out var currentVersion)
            || !SemVersion.TryParse(metadata.Version, out var targetVersion)
            || targetVersion <= currentVersion)
        {
            throw new InvalidOperationException(
                $"下载记录版本（{DisplayVersion(metadata.Version)}）不高于当前版本（{DisplayVersion(CurrentVersion)}），"
                + "已拒绝执行；请重新检查并下载。");
        }

        var installDir = InstallInfo.InstallDir;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new InvalidOperationException("无法确定当前安装目录，未启动安装程序。");
        }

        var arguments = AppInstallInfo.BuildSilentUpgradeArguments(installDir);
        _logger?.Log(
            LogLevel.Info,
            $"Update: 旁车校验通过（{ChecksumManifest.Short(actualHash)}，版本 {DisplayVersion(metadata.Version)}），"
            + $"启动安装程序 \"{setupPath}\" {arguments}");
        InstallerLauncher(setupPath, arguments);
    }

    /// <summary>读取并校验旁车文件；失败时给出可读原因（调用方必须拒绝执行）。</summary>
    private VerifiedSetupMetadata? ReadVerifiedSetupMetadata(
        string metadataPath,
        string setupPath,
        out string error)
    {
        error = "";
        if (!File.Exists(metadataPath))
        {
            error = "缺少安装包下载校验信息（" + metadataPath + "），无法确认安装包完整性，请重新下载。";
            return null;
        }

        VerifiedSetupMetadata? metadata;
        try
        {
            metadata = AtomicFile.ReadJson<VerifiedSetupMetadata>(metadataPath);
        }
        catch (Exception exception)
        {
            error = "安装包下载校验信息无法解析（" + metadataPath + "）：" + exception.Message + "。";
            return null;
        }

        if (metadata is null
            || !ChecksumManifest.IsSha256(metadata.Sha256)
            || metadata.Size <= 0
            || string.IsNullOrWhiteSpace(metadata.Version)
            || !SemVersion.TryParse(metadata.Version.Trim().TrimStart('v', 'V'), out _))
        {
            error = "安装包下载校验信息不完整（" + metadataPath + "），缺少有效的 sha256/version/size。";
            return null;
        }

        string recordedPath;
        try
        {
            recordedPath = Path.GetFullPath(metadata.SetupPath ?? "");
        }
        catch
        {
            error = "安装包下载校验信息里的路径无效（" + metadataPath + "）。";
            return null;
        }

        if (!DataRootResolution.PathEquals(recordedPath, Path.GetFullPath(setupPath)))
        {
            error = "安装包下载校验信息对应的是另一个文件（" + metadata.SetupPath + "），与当前安装包不一致。";
            return null;
        }

        return metadata;
    }

    /// <summary>比较安装包内嵌 ProductVersion 与旁车记录；都按 semver（忽略 v 前缀 / +build）。</summary>
    private static bool SameVersion(string? left, string? right)
    {
        static string Normalize(string? value) =>
            (value ?? "").Trim().TrimStart('v', 'V').Split('+')[0].Trim();

        var leftText = Normalize(left);
        var rightText = Normalize(right);
        if (leftText.Length == 0 || rightText.Length == 0) return false;
        if (SemVersion.TryParse(leftText, out var leftVersion) && SemVersion.TryParse(rightText, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 用户可见文本里的版本号（P3）。Inno 把 AppVersion 写进 ProductVersion 时会补填充空格
    /// （例如 <c>"0.2.0      "</c>）；<see cref="SameVersion"/> 已按 Trim 后的值比较，展示侧同样要
    /// 去掉首尾空白与 v 前缀，否则报错/日志句子中间会拖出一段可疑空白。空值统一显示“未知”。
    /// </summary>
    private static string DisplayVersion(string? value)
    {
        var text = (value ?? "").Trim().TrimStart('v', 'V').Trim();
        return text.Length == 0 ? "未知" : text;
    }

    /// <summary>默认的安装包版本读取方式（Inno 把 AppVersion 写进 ProductVersion）。失败返回 null。</summary>
    private static string? ReadSetupProductVersion(string setupPath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(setupPath).ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>默认的安装程序启动方式（测试会替换 <see cref="InstallerLauncher"/> 以免真的执行）。</summary>
    private static void LaunchInstaller(string setupPath, string arguments)
    {
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
    ///
    /// P3-R16：marker 现在写绝对路径；旧版写下的相对路径按**数据根**解析（不再依赖进程 CWD）。
    /// </summary>
    public void ConsumePendingSetupCleanup()
    {
        try
        {
            var marker = PendingSetupCleanupPath;
            if (!File.Exists(marker)) return;

            var lines = File.ReadAllLines(marker);
            var setupPath = ResolveMarkerSetupPath(lines.Length > 0 ? lines[0].Trim() : "");
            var oldVersionText = (lines.Length > 1 ? lines[1].Trim() : "").Split('+')[0].TrimStart('v', 'V');

            if (string.IsNullOrWhiteSpace(setupPath)
                || Path.GetFileName(setupPath) != AppInstallInfo.SetupAssetName
                || !SemVersion.TryParse(oldVersionText, out var oldVersion)
                || !SemVersion.TryParse(CurrentVersion.Split('+')[0], out var currentVersion)
                || currentVersion <= oldVersion
                || !File.Exists(setupPath))
            {
                File.Delete(marker);
                if (!string.IsNullOrWhiteSpace(setupPath)) TryDelete(VerifiedSetupMetadataPathFor(_paths, setupPath));
                return;
            }

            try
            {
                File.Delete(setupPath);
                TryDelete(VerifiedSetupMetadataPathFor(_paths, setupPath));
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

    /// <summary>
    /// P3-R16：marker 里的路径解析。新版本写绝对路径；旧版本可能写相对路径，按 data root 解析
    /// （旧实现的消费端按进程 CWD 解析，从计划任务/托盘启动时会删错或删不到）。
    /// </summary>
    private string? ResolveMarkerSetupPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(_paths.DataRoot, raw));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>数据根下的旁车文件路径（与安装包下载目录无关，避免污染 Downloads）。</summary>
    internal static string VerifiedSetupMetadataPathFor(LoomPaths paths, string setupPath)
    {
        var fileName = Path.GetFileName(setupPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = AppInstallInfo.SetupAssetName;
        return Path.Combine(paths.DataRoot, "updates", fileName + ".meta.json");
    }

    /// <summary>下载成功后写旁车（P2-R1）：原子写，失败则让下载失败，绝不留下“可 apply 但无校验”的状态。</summary>
    private void WriteVerifiedSetupMetadata(string setupPath, string sha256, long size, string version)
    {
        var metadataPath = VerifiedSetupMetadataPath;
        var metadata = new VerifiedSetupMetadata
        {
            SetupPath = Path.GetFullPath(setupPath),
            Sha256 = sha256,
            Version = version.Trim().TrimStart('v', 'V'),
            Size = size,
            DownloadedAt = DateTimeOffset.Now,
        };
        try
        {
            AtomicFile.WriteJson(metadataPath, metadata);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "安装包已下载，但无法写入下载校验信息（" + metadataPath + "）：" + exception.Message
                + "；为避免执行未校验的安装包，本次更新未标记为可执行。", exception);
        }

        _logger?.Log(
            LogLevel.Info,
            $"Update: 安装包校验信息已写入 {metadataPath}"
            + $"（sha256 {ChecksumManifest.Short(sha256)}，版本 {metadata.Version}，{size} bytes）");
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
            if (_configMutator is not null)
            {
                // P2-4：后台时间戳走 LoomService.MutateConfig（锁内读最新 → 只改这一个字段 → 保存），
                // 不再拿整份旧 config 覆盖用户刚保存的设置。
                _configMutator(config => config.LastUpdateCheckUtc = DateTimeOffset.UtcNow);
                return;
            }

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

    /// <summary>P3-R9：解析结果缓存，getter 只做只读返回；首次解析后不再创建/删除探针文件。</summary>
    private string ResolveDownloadPathCached()
    {
        lock (_downloadPathGate)
        {
            if (_downloadPathResolved) return _cachedResolvedDownloadPath!;
            var resolved = ResolveDownloadPathCore();
            _cachedResolvedDownloadPath = resolved;
            _downloadPathResolved = true;
            return resolved;
        }
    }

    private string ResolveDownloadPathCore()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // P3-R9：默认解析系统 Downloads；测试可用 DownloadsPathProvider 注入并统计调用次数。
                var downloads = DownloadsPathProvider();
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

    /// <summary>默认 Downloads 目录解析（shell32 已知文件夹）；非 Windows/失败返回 null。</summary>
    private static string? DefaultDownloadsPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return GetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
        }
        catch
        {
            return null;
        }
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
            // P3-R16：统一写绝对路径；旧版本写下的相对值在消费端按 data root 解析。
            var absoluteSetupPath = Path.GetFullPath(setupPath);
            File.WriteAllText(
                PendingSetupCleanupPath,
                absoluteSetupPath + System.Environment.NewLine + CurrentVersion.Split('+')[0] + System.Environment.NewLine);
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
