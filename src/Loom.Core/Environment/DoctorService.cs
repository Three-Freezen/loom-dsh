using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Environment;

/// <summary>
/// "Loom doctor": read-only environment self-check. Check names are stable English keys
/// (UI/report keys); details are Chinese user-facing text.
/// </summary>
public sealed class DoctorService : IDoctorService
{
    private const long MinimumFreeDiskBytes = 512L * 1024 * 1024;
    private const long UpdaterCacheWarnBytes = 1L * 1024 * 1024 * 1024;

    private readonly LoomPaths _paths;
    private readonly ILogger _logger;

    public DoctorService(LoomPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<DoctorResult> RunAsync(
        DshInstallation? installation,
        LoomPaths paths,
        LoomConfig config,
        CancellationToken cancellationToken = default)
    {
        paths ??= _paths;
        var checks = new List<DoctorCheck>();
        var warnings = new List<string>();

        void Add(string name, bool ok, string detail) =>
            checks.Add(new DoctorCheck { Name = name, Ok = ok, Detail = detail });

        // ---------------------------------------------------------- installation
        if (installation is null || string.IsNullOrWhiteSpace(installation.InstallDir))
        {
            Add("installation", false, "未找到 DeepSeek Harness 安装目录（可在设置中手动指定安装目录后重新定位）");
        }
        else
        {
            Add("installation", Directory.Exists(installation.InstallDir), installation.InstallDir);
            Add("profile", Directory.Exists(installation.ProfileDir), installation.ProfileDir + "（profile 目录）");

            var runtimeManifest = Path.Combine(installation.InstallDir, @"resources\runtime\primary-runtime\runtime.json");
            Add("runtime-manifest", File.Exists(runtimeManifest), File.Exists(runtimeManifest) ? runtimeManifest : $"缺失：{runtimeManifest}");

            var cliPath = installation.CliPath;
            Add("dsh-cli", File.Exists(cliPath), File.Exists(cliPath) ? cliPath : $"缺失：{cliPath}");

            var electronExe = installation.ElectronExe;
            Add("electron", File.Exists(electronExe), File.Exists(electronExe) ? electronExe : $"缺失：{electronExe}");

            var appAsar = installation.AppAsar;
            Add("app-asar", File.Exists(appAsar), File.Exists(appAsar) ? appAsar : $"缺失：{appAsar}");

            // ------------------------------------------------------- profile locks
            try
            {
                var locks = new LockManager(new ProcessProbe(_logger), _logger).Inspect(installation);
                var detail = locks.Busy
                    ? "占用：" + string.Join("；", locks.Reasons)
                    : (string.IsNullOrWhiteSpace(locks.Detail) ? "无锁占用" : "无锁占用（" + locks.Detail + "）");
                Add("profile-locks", !locks.Busy, detail);
            }
            catch (Exception exception)
            {
                Add("profile-locks", false, "锁检测失败：" + exception.Message);
            }

            // ----------------------------------------------------- version sanity
            var appKnown = SemVersion.TryParse(installation.AppVersion, out _);
            var dshKnown = SemVersion.TryParse(installation.DshVersion, out _);
            if (!dshKnown)
            {
                Add("version-consistency", false, $"运行时版本未知（dshVersion={installation.DshVersion}）；Electron helper 可能不可用");
            }
            else
            {
                Add(
                    "version-consistency",
                    appKnown,
                    $"应用 {installation.AppVersion} / 运行时 {installation.DshVersion}" + (appKnown ? "" : "（应用版本无法解析）"));
            }
        }

        // ----------------------------------------------------------- electron helper
        var helperPath = paths.HelperPath;
        var helperOk = File.Exists(helperPath);
        long helperSize = 0;
        if (helperOk)
        {
            try
            {
                helperSize = new FileInfo(helperPath).Length;
            }
            catch
            {
                helperSize = 0;
            }
        }

        Add(
            "helper",
            helperOk && helperSize > 0,
            helperOk ? $"{helperPath}（{helperSize} bytes）" : $"缺失：{helperPath}（评估器将只能返回 unknown）");

        // ------------------------------------------------------------ updater cache
        try
        {
            var updaterRoot = installation?.UpdaterRoot;
            if (string.IsNullOrWhiteSpace(updaterRoot))
            {
                var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                updaterRoot = string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, DshDefaults.UpdaterFolderName);
            }

            if (updaterRoot is null || !Directory.Exists(updaterRoot))
            {
                Add("updater-cache", true, "更新器缓存目录不存在（尚未下载过更新）");
            }
            else
            {
                var bytes = FileSystemUtil.DirectorySize(updaterRoot);
                var mb = bytes / (1024.0 * 1024.0);
                if (bytes > UpdaterCacheWarnBytes) warnings.Add($"更新器缓存占用 {mb:0} MB（只读展示，Loom 不会删除）");
                Add("updater-cache", true, $"{updaterRoot}（{mb:0} MB，只读占用）");
            }
        }
        catch (Exception exception)
        {
            Add("updater-cache", false, "更新器缓存检查失败：" + exception.Message);
        }

        // -------------------------------------------------------------- data root
        var dataRoot = paths.DataRoot;
        var dataWritable = TryWriteProbe(dataRoot, "loom-doctor-probe.tmp");
        Add("data-root-writable", dataWritable, dataWritable ? dataRoot : $"不可写：{dataRoot}（检查磁盘/权限）");

        // -------------------------------------------------------------- disk space
        var available = FileSystemUtil.AvailableBytes(dataRoot);
        var availableMb = available == long.MaxValue ? -1 : available / (1024.0 * 1024.0);
        if (availableMb < 0)
        {
            Add("disk-space", true, "无法获取磁盘剩余空间");
        }
        else if (available < MinimumFreeDiskBytes)
        {
            Add("disk-space", false, $"剩余 {availableMb:0} MB，低于建议的 512 MB");
        }
        else
        {
            Add("disk-space", true, $"剩余 {availableMb:0} MB");
        }

        // ----------------------------------------------------------------- autostart
        try
        {
            var autostart = new AutostartService(_logger);
            var enabled = autostart.IsEnabled();
            var command = autostart.GetRegisteredCommand();
            Add(
                "autostart",
                true,
                enabled ? $"已启用：{command ?? "(无法读取命令)"}" : "未启用");
        }
        catch (Exception exception)
        {
            Add("autostart", false, "自启状态检查失败：" + exception.Message);
        }

        // ------------------------------------------------------------------- config
        Add(
            "config",
            true,
            $"installDir={(string.IsNullOrWhiteSpace(config.InstallDir) ? "自动" : config.InstallDir)}；" +
            $"policy={config.AutoUpgradePolicy}；keepSnapshots={config.KeepSnapshots}；theme={config.Theme}");

        cancellationToken.ThrowIfCancellationRequested();

        var healthy = checks.All(check => check.Ok);
        var failed = checks.Where(check => !check.Ok).Select(check => check.Name).ToList();
        var summary = healthy
            ? "环境正常"
            : "存在问题：" + string.Join("、", failed);
        if (warnings.Count > 0) summary += "；提示：" + string.Join("；", warnings);

        return new DoctorResult
        {
            Healthy = healthy,
            Checks = checks,
            Summary = summary,
        };
    }

    private static bool TryWriteProbe(string directory, string fileName)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, fileName);
            File.WriteAllText(probePath, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
