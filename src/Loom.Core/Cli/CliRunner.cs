using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Ipc;
using Loom.Core.Models;
using Loom.Core.Update;

namespace Loom.Core.Cli;

/// <summary>Shared CLI command implementation used by the standalone CLI and the tray's queue executor.</summary>
public sealed class CliRunner
{
    private readonly ILoomService _loom;
    private readonly IUiCommandHandler? _ui;

    public CliRunner(ILoomService loom, IUiCommandHandler? ui = null)
    {
        _loom = loom;
        _ui = ui;
    }

    public async Task<int> RunAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        if (invocation.Error is not null)
        {
            return Error(invocation, stdout, stderr, invocation.Error, ExitCodes.Busy);
        }

        if (invocation.Help || string.Equals(invocation.Command, "help", StringComparison.OrdinalIgnoreCase))
        {
            WriteHelp(stdout);
            return ExitCodes.Success;
        }

        try
        {
            return invocation.Command.ToLowerInvariant() switch
            {
                "status" => await StatusAsync(invocation, stdout, cancellationToken).ConfigureAwait(false),
                "snapshot" => await SnapshotAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                "check" => await CheckAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                "restore" => await RestoreAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                "report" => await ReportAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                "doctor" => await DoctorAsync(invocation, stdout, cancellationToken).ConfigureAwait(false),
                "migrate-data" => await MigrateDataAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                "update" => await UpdateAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                "install-autostart" => await AutostartAsync(true, invocation, stdout, cancellationToken).ConfigureAwait(false),
                "uninstall-autostart" => await AutostartAsync(false, invocation, stdout, cancellationToken).ConfigureAwait(false),
                "version" => Version(invocation, stdout),
                "ui" => await UiAsync(invocation, stdout, stderr, cancellationToken).ConfigureAwait(false),
                _ => Error(invocation, stdout, stderr, $"未知命令：{invocation.Command}", ExitCodes.Busy),
            };
        }
        catch (OperationCanceledException)
        {
            return Error(invocation, stdout, stderr, "操作已取消", ExitCodes.Failed);
        }
        catch (LoomException exception)
        {
            return Error(invocation, stdout, stderr, exception.Message, MapExitCode(exception.Code));
        }
        catch (Exception exception)
        {
            return Error(invocation, stdout, stderr, "操作失败：" + exception.Message, ExitCodes.Failed);
        }
    }

    private async Task<int> StatusAsync(CliInvocation invocation, TextWriter stdout, CancellationToken cancellationToken)
    {
        await _loom.RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        var status = _loom.GetStatus();
        if (invocation.Json)
        {
            WriteJson(stdout, status);
            return ExitCodes.Success;
        }

        stdout.WriteLine($"Loom for DSH {LoomPaths.ToolVersion}");
        stdout.WriteLine($"状态       : {TrayStateLabel(status.TrayState)}");
        stdout.WriteLine($"提示       : {status.Headline}");
        if (!string.IsNullOrEmpty(status.Detail)) stdout.WriteLine($"说明       : {status.Detail}");
        if (status.Installation is { Found: true } installation)
        {
            stdout.WriteLine($"安装目录   : {installation.InstallDir}");
            stdout.WriteLine($"profile    : {installation.ProfileDir}");
            stdout.WriteLine($"应用版本   : {installation.AppVersion}");
            stdout.WriteLine($"运行时版本 : {installation.DshVersion}");
        }
        else
        {
            stdout.WriteLine("安装目录   : 未定位");
        }

        stdout.WriteLine($"最近快照   : {status.LastSnapshotAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "无"}");
        stdout.WriteLine($"保护插件   : {status.ProtectedPlugins}");
        stdout.WriteLine($"快照数量   : {status.SnapshotCount}（{FormatBytes(status.SnapshotBytes)}）");
        stdout.WriteLine($"待处理问题 : {status.PendingIssues}");
        stdout.WriteLine($"DSH 运行中 : {(status.AppRunning ? "是" : "否")}");
        if (!string.IsNullOrEmpty(status.LastReportPath))
        {
            stdout.WriteLine($"最近报告   : {status.LastReportPath}");
        }

        return ExitCodes.Success;
    }

    private async Task<int> SnapshotAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (invocation.DryRun)
        {
            stdout.WriteLine("快照没有 --dry-run 模式；改为运行 doctor 检查环境，或直接执行 snapshot。");
            if (invocation.Json) WriteJson(stdout, new { ok = false, code = "E_DRY_RUN_UNSUPPORTED" });
            return ExitCodes.Failed;
        }

        var trigger = invocation.Positionals.Count > 0 && string.Equals(invocation.Positionals[0], "pre-update", StringComparison.OrdinalIgnoreCase)
            ? SnapshotTrigger.PreUpdate
            : SnapshotTrigger.Manual;
        var request = new SnapshotRequest { Trigger = trigger, Full = trigger == SnapshotTrigger.PreUpdate };
        var progress = new Progress<OperationProgress>(item =>
        {
            if (!invocation.Quiet) stderr.WriteLine($"[{item.Stage}] {item.Message}");
        });

        var outcome = await _loom.TakeSnapshotAsync(request, progress, cancellationToken).ConfigureAwait(false);
        if (invocation.Json)
        {
            WriteJson(stdout, new
            {
                ok = outcome.Success,
                operationId = outcome.OperationId,
                snapshot = outcome.Snapshot,
                message = outcome.Message,
                warnings = outcome.Warnings,
                report = outcome.Report?.MarkdownPath,
            });
        }
        else if (outcome.Success)
        {
            stdout.WriteLine($"快照完成：{outcome.Snapshot?.Id}");
            stdout.WriteLine($"路径    ：{outcome.Snapshot?.Path}");
            stdout.WriteLine($"插件    ：{outcome.Snapshot?.PluginCount} 个");
            stdout.WriteLine($"报告    ：{outcome.Report?.MarkdownPath}");
        }
        else
        {
            stderr.WriteLine($"快照失败：{outcome.Message}");
        }

        return outcome.Success
            ? ExitCodes.Success
            : outcome.ErrorCode is not null
                ? MapExitCode(outcome.ErrorCode)
                : MapOutcome(outcome.Message);
    }

    private async Task<int> CheckAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<OperationProgress>(item =>
        {
            if (!invocation.Quiet) stderr.WriteLine($"[{item.Stage}] {item.Message}");
        });
        var outcome = await _loom.RunCheckAsync(new CheckRequest { Auto = false }, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Success || outcome.Result is null)
        {
            if (invocation.Json) WriteJson(stdout, new { ok = false, code = outcome.ErrorCode, message = outcome.Message });
            else stderr.WriteLine("检查失败：" + outcome.Message);
            return outcome.ErrorCode is not null ? MapExitCode(outcome.ErrorCode) : ExitCodes.Failed;
        }

        var result = outcome.Result;
        if (invocation.Json)
        {
            WriteJson(stdout, new
            {
                ok = true,
                operationId = outcome.OperationId,
                result,
                report = outcome.Report?.MarkdownPath,
            });
        }
        else
        {
            stdout.WriteLine($"兼容性检查（应用 {result.Versions.AppVersionAfter} / 运行时 {result.Versions.DshVersionAfter}）");
            stdout.WriteLine($"摘要          : {result.Summary}");
            stdout.WriteLine($"证据等级      : {EvidenceLabel(result.Evidence)}");
            stdout.WriteLine($"不兼容/未验证/未知: {result.IncompatibleCount}/{result.UnverifiedCount}/{result.UnknownCount}");
            stdout.WriteLine($"可文件级还原  : {(result.RestorePossible ? "是" : "否")}");
            if (result.RemovedBundles.Count > 0) stdout.WriteLine("缺失 bundles  : " + string.Join(", ", result.RemovedBundles));
            if (result.MissingDependencies.Count > 0) stdout.WriteLine("缺失依赖      : " + string.Join(", ", result.MissingDependencies));
            foreach (var issue in result.Issues)
            {
                stdout.WriteLine($"  - [{issue.Severity}] {issue.Message}");
            }

            if (!string.IsNullOrEmpty(outcome.Report?.MarkdownPath)) stdout.WriteLine($"报告          : {outcome.Report?.MarkdownPath}");
        }

        return result.Issues.Count == 0 ? ExitCodes.Success : ExitCodes.NeedsManual;
    }

    private async Task<int> RestoreAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new RestoreRequest
        {
            DryRun = invocation.DryRun,
            Force = invocation.Force || invocation.Yes,
            SnapshotId = invocation.SnapshotId,
        };
        var progress = new Progress<OperationProgress>(item =>
        {
            if (!invocation.Quiet) stderr.WriteLine($"[{item.Stage}] {item.Message}");
        });
        var result = await _loom.RestoreAsync(request, progress, cancellationToken).ConfigureAwait(false);

        if (invocation.Json)
        {
            WriteJson(stdout, new
            {
                ok = result.Outcome is OperationOutcome.Succeeded or OperationOutcome.DryRun,
                outcome = result.Outcome.ToString(),
                operationId = result.OperationId,
                restored = result.RestoredCount,
                skipped = result.SkippedCount,
                conflicts = result.ConflictCount,
                restartRequired = result.RestartRequired,
                messages = result.Messages,
                plan = result.Plan,
                report = result.ReportPath,
            });
        }
        else
        {
            stdout.WriteLine($"还原结果：{OutcomeLabel(result.Outcome)}");
            foreach (var message in result.Messages) stdout.WriteLine("  " + message);
            stdout.WriteLine($"已还原 {result.RestoredCount} 项，跳过 {result.SkippedCount} 项，冲突 {result.ConflictCount} 项");
            if (result.RestartRequired) stdout.WriteLine("请重启 DSH 使改动生效。");
            if (!string.IsNullOrEmpty(result.ReportPath)) stdout.WriteLine("报告：" + result.ReportPath);
        }

        return result.Outcome switch
        {
            OperationOutcome.Succeeded or OperationOutcome.DryRun => ExitCodes.Success,
            OperationOutcome.NeedsManual => ExitCodes.NeedsManual,
            _ => result.ErrorCode is not null ? MapExitCode(result.ErrorCode) : ExitCodes.Failed,
        };
    }

    private async Task<int> ReportAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var report = await _loom.GetLatestReportAsync(cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            stderr.WriteLine("还没有报告。");
            return ExitCodes.Unavailable;
        }

        if (invocation.Json)
        {
            stdout.WriteLine(report.Json);
        }
        else
        {
            stdout.WriteLine(report.Markdown);
        }

        return ExitCodes.Success;
    }

    private async Task<int> DoctorAsync(CliInvocation invocation, TextWriter stdout, CancellationToken cancellationToken)
    {
        var result = await _loom.RunDoctorAsync(cancellationToken).ConfigureAwait(false);
        if (invocation.Json)
        {
            WriteJson(stdout, result);
        }
        else
        {
            stdout.WriteLine("环境体检");
            foreach (var check in result.Checks)
            {
                stdout.WriteLine($"  [{(check.Ok ? "OK" : "!!")}] {check.Name}: {check.Detail}");
            }

            stdout.WriteLine(result.Healthy ? "结论：环境正常" : "结论：存在问题（见上）");
        }

        return result.Healthy ? ExitCodes.Success : ExitCodes.NeedsManual;
    }

    private async Task<int> AutostartAsync(
        bool enabled,
        CliInvocation invocation,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        await _loom.SetAutostartAsync(enabled, cancellationToken).ConfigureAwait(false);
        if (invocation.Json) WriteJson(stdout, new { ok = true, autostart = enabled });
        else stdout.WriteLine(enabled ? "已启用开机自启（HKCU Run）。" : "已关闭开机自启。");
        return ExitCodes.Success;
    }

    private Task<int> MigrateDataAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = _loom.Paths.DataRoot;
        var source = DataRootResolver.LegacyDefaultRoot;
        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = source,
            TargetRoot = target,
            DryRun = invocation.DryRun,
            RemoveSource = invocation.RemoveSource,
        });

        if (invocation.Json)
        {
            WriteJson(stdout, new
            {
                ok = result.Success,
                dryRun = result.DryRun,
                sourceRoot = result.SourceRoot,
                targetRoot = result.TargetRoot,
                skippedReason = result.SkippedReason,
                copied = result.FilesCopied,
                skipped = result.FilesSkipped,
                pathsRewritten = result.PathsRewritten,
                markerPath = result.MarkerPath,
                markerWritten = result.MarkerWritten,
                sourceRemoved = result.SourceRemoved,
                errorCode = result.ErrorCode,
                errors = result.Errors,
                message = result.Message,
            });
        }
        else
        {
            stdout.WriteLine("数据目录迁移");
            stdout.WriteLine($"来源      ：{result.SourceRoot}");
            stdout.WriteLine($"目标      ：{result.TargetRoot}");
            stdout.WriteLine("说明      ：" + result.Message);
            if (result.SkippedReason is null && !result.DryRun)
            {
                stdout.WriteLine($"文件      ：复制 {result.FilesCopied} 个，跳过 {result.FilesSkipped} 个");
            }

            if (result.PathsRewritten > 0) stdout.WriteLine($"路径重写  ：{result.PathsRewritten} 处");
            if (result.MarkerWritten) stdout.WriteLine($"迁移标记  ：{result.MarkerPath}");
            if (result.SourceRemoved) stdout.WriteLine("已删除旧数据目录。");
            foreach (var error in result.Errors) stderr.WriteLine("  - " + error);
        }

        if (result.Success) return Task.FromResult(ExitCodes.Success);
        return Task.FromResult(
            result.ErrorCode is "same-root" or "invalid-root" ? ExitCodes.Unavailable : ExitCodes.Failed);
    }

    private async Task<int> UpdateAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (invocation.UpdateApply && invocation.UpdateDownload)
        {
            return Error(invocation, stdout, stderr, "不能同时指定 --download 与 --apply。", ExitCodes.Busy);
        }

        var updates = _loom.Updates;
        if (!string.IsNullOrWhiteSpace(invocation.UpdateSource))
        {
            updates.SourceOverride = invocation.UpdateSource;
        }

        if (!string.IsNullOrWhiteSpace(invocation.UpdateDownloadDir))
        {
            // Override is a directory; the asset keeps its fixed name (update/cleanup logic depends on it).
            updates.DownloadPathOverride = Path.Combine(
                invocation.UpdateDownloadDir,
                AppInstallInfo.SetupAssetName);
        }

        var result = await updates.CheckAsync(cancellationToken).ConfigureAwait(false);
        var available = result is not null;
        var downloaded = false;

        if (available && invocation.UpdateDownload)
        {
            downloaded = await updates.DownloadAsync(cancellationToken).ConfigureAwait(false);
            if (!downloaded)
            {
                return Error(
                    invocation,
                    stdout,
                    stderr,
                    "下载安装包失败：" + (updates.ErrorText ?? "未知错误"),
                    ExitCodes.Failed);
            }
        }

        if (available && invocation.UpdateApply)
        {
            if (SingleInstance.IsTrayRunning())
            {
                return Error(
                    invocation,
                    stdout,
                    stderr,
                    "托盘正在运行：请在托盘菜单 → 设置 → 更新与兼容里点“重启并更新”，"
                    + "或在退出托盘后运行 loom-dsh update --apply。",
                    ExitCodes.Busy);
            }

            if (!File.Exists(updates.SetupDownloadPath))
            {
                return Error(
                    invocation,
                    stdout,
                    stderr,
                    "安装包尚未下载，请先运行 loom-dsh update --download。",
                    ExitCodes.Failed);
            }

            try
            {
                updates.ApplyAndRestart();
            }
            catch (Exception exception)
            {
                return Error(invocation, stdout, stderr, exception.Message, ExitCodes.Failed);
            }

            stdout.WriteLine("安装程序已启动，Loom 即将退出以完成替换；升级完成后会自动重启。");
            return ExitCodes.Success;
        }

        if (invocation.Json)
        {
            WriteJson(stdout, new
            {
                ok = updates.State != UpdateState.Failed,
                state = updates.State.ToString(),
                currentVersion = updates.CurrentVersion,
                newVersion = updates.NewVersion,
                inAppUpdateSupported = updates.InAppUpdateSupported,
                releaseUrl = updates.ReleaseUrl,
                downloadPath = updates.SetupDownloadPath,
                downloaded,
                message = updates.MessageText,
                error = updates.ErrorText,
            });
        }
        else
        {
            stdout.WriteLine($"当前版本：{updates.CurrentVersion}");
            switch (updates.State)
            {
                case UpdateState.Failed:
                    stderr.WriteLine("检查更新失败：" + (updates.ErrorText ?? "未知错误"));
                    return ExitCodes.Failed;
                case UpdateState.Available:
                    stdout.WriteLine($"发现新版本：{updates.NewVersion}");
                    stdout.WriteLine($"发布页  ：{updates.ReleaseUrl}");
                    if (updates.InAppUpdateSupported)
                    {
                        stdout.WriteLine("下载    ：loom-dsh update --download（或在设置页一键下载/安装）");
                    }
                    else
                    {
                        stdout.WriteLine("当前为免安装/开发版：应用内更新不可用，请到发布页手动下载安装包。");
                    }

                    break;
                case UpdateState.UpToDate:
                    stdout.WriteLine(updates.MessageText ?? "已是最新版本。");
                    break;
                default:
                    stdout.WriteLine($"状态：{updates.State}");
                    break;
            }

            if (downloaded)
            {
                stdout.WriteLine($"安装包已下载：{updates.SetupDownloadPath}");
                stdout.WriteLine("可在设置页点“重启并更新”（或退出托盘后运行 loom-dsh update --apply）。");
            }
        }

        return updates.State == UpdateState.Failed ? ExitCodes.Failed : ExitCodes.Success;
    }

    private int Version(CliInvocation invocation, TextWriter stdout)
    {
        var status = _loom.GetStatus();
        var resolution = DataRootResolver.Resolve(invocation.DataRoot);
        var payload = new
        {
            tool = "loom-dsh",
            version = LoomPaths.ToolVersion,
            dataRoot = _loom.Paths.DataRoot,
            dataRootSource = resolution.Source.ToString(),
            installedBuild = resolution.IsInstalledBuild,
            legacyDataRoot = resolution.LegacyRoot,
            dsh = status.Installation,
        };
        if (invocation.Json)
        {
            WriteJson(stdout, payload);
        }
        else
        {
            stdout.WriteLine($"loom-dsh {LoomPaths.ToolVersion}");
            stdout.WriteLine($"数据目录：{_loom.Paths.DataRoot}（{resolution.SourceLabel}）");
            if (resolution.IsInstalledBuild)
            {
                stdout.WriteLine($"旧数据目录（迁移来源）：{resolution.LegacyRoot}");
            }

            if (status.Installation is { Found: true } installation)
            {
                stdout.WriteLine($"DSH：{installation.InstallDir}（应用 {installation.AppVersion} / 运行时 {installation.DshVersion}）");
            }
            else
            {
                stdout.WriteLine("DSH：未定位");
            }
        }

        return ExitCodes.Success;
    }

    private async Task<int> UiAsync(
        CliInvocation invocation,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (_ui is null)
        {
            stderr.WriteLine("当前上下文没有可用的界面宿主。请先启动托盘（loom-dsh --tray）。");
            return ExitCodes.Unavailable;
        }

        var code = await _ui.ShowAsync(invocation.UiTarget ?? "popup", cancellationToken).ConfigureAwait(false);
        if (invocation.Json) WriteJson(stdout, new { ok = code == ExitCodes.Success, target = invocation.UiTarget });
        return code;
    }

    private int Error(CliInvocation invocation, TextWriter stdout, TextWriter stderr, string message, int exitCode)
    {
        if (invocation.Json)
        {
            WriteJson(stdout, new { ok = false, message });
        }
        else
        {
            stderr.WriteLine(message);
        }

        return exitCode;
    }

    private bool WriteJson<T>(TextWriter stdout, T value)
    {
        stdout.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
        return true;
    }

    private static int MapOutcome(string message) =>
        message.Contains("人工", StringComparison.Ordinal)
            ? ExitCodes.NeedsManual
            : message.Contains("占用", StringComparison.Ordinal) || message.Contains("锁", StringComparison.Ordinal)
                ? ExitCodes.Busy
                : ExitCodes.Failed;

    private static int MapExitCode(string code) => code switch
    {
        LoomErrorCodes.NotFound => ExitCodes.Unavailable,
        LoomErrorCodes.Unsupported => ExitCodes.Unavailable,
        LoomErrorCodes.Busy or LoomErrorCodes.LockTimeout => ExitCodes.Busy,
        LoomErrorCodes.NeedsManual => ExitCodes.NeedsManual,
        _ => ExitCodes.Failed,
    };

    public static string TrayStateLabel(TrayStatus state) => state switch
    {
        TrayStatus.Normal => "正常",
        TrayStatus.Busy => "处理中",
        TrayStatus.AwaitingExit => "待退出/待处理",
        TrayStatus.NeedsManual => "需人工",
        TrayStatus.Failed => "失败",
        TrayStatus.NotFound => "未找到 DSH",
        _ => state.ToString(),
    };

    private static string EvidenceLabel(EvidenceLevel level) => level switch
    {
        EvidenceLevel.Strong => "强",
        EvidenceLevel.Medium => "中",
        EvidenceLevel.Weak => "弱",
        _ => "无",
    };

    private static string OutcomeLabel(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Succeeded => "成功",
        OperationOutcome.Failed => "失败",
        OperationOutcome.NeedsManual => "需要人工处理",
        OperationOutcome.Skipped => "已跳过",
        OperationOutcome.Cancelled => "已取消",
        OperationOutcome.DryRun => "演练（未写入）",
        _ => outcome.ToString(),
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("""
                         loom-dsh - Loom for DSH：DSH 插件更新守护（快照 / 兼容检查 / 文件级还原）

                         用法:
                           loom-dsh [--tray]                 启动托盘（默认；无参数时同）
                           loom-dsh status [--json]          显示当前状态
                           loom-dsh snapshot [pre-update]    创建快照（pre-update 为完整快照）
                           loom-dsh check [--json]           检查插件兼容性/恢复证据
                           loom-dsh restore [--dry-run] [--force] [--snapshot <id>]
                                                             文件级还原（--dry-run 只出计划）
                           loom-dsh report [--json]          输出最近一次报告
                           loom-dsh doctor [--json]          环境体检
                           loom-dsh migrate-data [--dry-run] [--json] [--remove-source]
                                                             把旧数据目录（默认 %LOCALAPPDATA%\Loom）迁移到当前数据根；
                                                             安装版首次启动会自动迁移（只复制不删除）
                           loom-dsh update [--download] [--apply] [--json]
                                                              检查 Loom 应用更新（GitHub Releases）；--download 下载，
                                                              --apply 静默升级（托盘需退出）
                           loom-dsh ui <popup|settings|wizard|report>
                                                             打开界面窗口（需托盘运行，会自动拉起）
                           loom-dsh install-autostart        启用开机自启（HKCU Run）
                           loom-dsh uninstall-autostart      关闭开机自启
                           loom-dsh version                  版本信息

                         通用参数:
                           --data-root <dir>    工具数据目录（默认：安装版 <安装目录>\Data；开发/绿色版 %LOCALAPPDATA%\Loom）
                           --install-dir <dir>  DSH 安装目录（覆盖自动定位）
                           --dsh-home <dir>     DSH 数据根（默认 %USERPROFILE%\.dsh）
                           --profile-dir <dir>  profile 目录（默认 <dsh-home>\profiles\desktop）
                           --log-level <lvl>    error|warn|info|debug
                           --json               机器可读输出
                           --quiet              不输出进度

                         数据目录优先级: --data-root > <exe目录>\Data\portable.flag（便携模式）
                                       > 已安装构建 <安装目录>\Data > 旧默认 %LOCALAPPDATA%\Loom

                         退出码: 0 成功 / 1 失败 / 2 待人工 / 3 不可用 / 4 忙
                         """);
    }
}
