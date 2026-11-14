using System.Globalization;
using System.Text;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Reporting;

/// <summary>
/// Writes <c>reports\report-&lt;opId&gt;.json</c> (machine readable) and
/// <c>report-&lt;opId&gt;.md</c> (human readable), UTF-8 without BOM.
/// Never writes cordis.patch.yml content, environment variables or credentials (design 13.8 / 15).
/// </summary>
public sealed class ReportService : Abstractions.IReportService
{
    public Task<ReportView> WriteAsync(
        OperationReport report,
        LoomPaths paths,
        CancellationToken cancellationToken = default)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(paths.ReportsDir);
        var operationId = SanitizeOperationId(report.OperationId);
        if (string.IsNullOrEmpty(operationId))
        {
            operationId = LoomPaths.NewOperationId(string.IsNullOrWhiteSpace(report.Kind) ? "op" : report.Kind);
            report.OperationId = operationId;
        }

        var jsonPath = Path.Combine(paths.ReportsDir, "report-" + operationId + ".json");
        var markdownPath = Path.Combine(paths.ReportsDir, "report-" + operationId + ".md");
        var json = JsonDefaults.Serialize(report);
        var markdown = BuildMarkdown(report, jsonPath, markdownPath);

        AtomicFile.WriteAllText(jsonPath, json);
        AtomicFile.WriteAllText(markdownPath, markdown);

        return Task.FromResult(new ReportView
        {
            JsonPath = jsonPath,
            MarkdownPath = markdownPath,
            Json = json,
            Markdown = markdown,
        });
    }

    public Task<ReportView?> ReadLatestAsync(LoomPaths paths, CancellationToken cancellationToken = default)
    {
        if (paths is null || !Directory.Exists(paths.ReportsDir)) return Task.FromResult<ReportView?>(null);

        var latest = Directory.EnumerateFiles(paths.ReportsDir, "report-*.json")
            .Select(path => new { path, stamp = SafeLastWrite(path) })
            .OrderByDescending(item => item.stamp)
            .ThenByDescending(item => Path.GetFileName(item.path), StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null) return Task.FromResult<ReportView?>(null);

        cancellationToken.ThrowIfCancellationRequested();
        return ReadAsync(latest.path, cancellationToken);
    }

    public Task<ReportView?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Task.FromResult<ReportView?>(null);
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(path);
        string jsonPath;
        string markdownPath;
        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
        {
            markdownPath = path;
            jsonPath = Path.ChangeExtension(path, ".json");
        }
        else
        {
            jsonPath = path;
            markdownPath = Path.ChangeExtension(path, ".md");
        }

        var json = File.Exists(jsonPath) ? ReadAllTextShared(jsonPath) : "";
        var markdown = File.Exists(markdownPath) ? ReadAllTextShared(markdownPath) : "";
        if (string.IsNullOrEmpty(json) && string.IsNullOrEmpty(markdown)) return Task.FromResult<ReportView?>(null);

        if (string.IsNullOrEmpty(markdown) && !string.IsNullOrEmpty(json))
        {
            // Keep the CLI human-readable even when the .md sidecar was deleted.
            try
            {
                var report = JsonDefaults.Deserialize<OperationReport>(json);
                if (report is not null) markdown = BuildMarkdown(report, jsonPath, markdownPath);
            }
            catch (Exception)
            {
                markdown = "";
            }
        }

        return Task.FromResult<ReportView?>(new ReportView
        {
            JsonPath = File.Exists(jsonPath) ? jsonPath : null,
            MarkdownPath = File.Exists(markdownPath) ? markdownPath : null,
            Json = json,
            Markdown = markdown,
        });
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string SanitizeOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(operationId.Length);
        foreach (var character in operationId)
        {
            builder.Append(invalid.Contains(character) || character == Path.DirectorySeparatorChar ? '_' : character);
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(OperationReport report, string jsonPath, string markdownPath)
    {
        var text = new StringBuilder();
        text.AppendLine("# Loom for DSH 操作报告");
        text.AppendLine();
        text.AppendLine($"- 操作：{KindLabel(report.Kind)}（{report.Kind}）");
        text.AppendLine($"- 操作 ID：{Safe(report.OperationId)}");
        text.AppendLine($"- 开始时间：{FormatTime(report.StartedAt)}");
        text.AppendLine($"- 结束时间：{FormatTime(report.FinishedAt)}");
        text.AppendLine($"- 结果：{OutcomeLabel(report.Outcome)}");
        text.AppendLine($"- 摘要：{Safe(report.Summary)}");
        text.AppendLine($"- 应用版本：{Safe(report.AppVersion ?? "unknown")}");
        text.AppendLine($"- 运行时版本：{Safe(report.DshVersion ?? "unknown")}");
        text.AppendLine($"- 证据等级：{EvidenceLabel(report.Evidence)}");
        text.AppendLine();

        if (report.Snapshot is not null)
        {
            text.AppendLine("## 快照");
            text.AppendLine();
            text.AppendLine($"- ID：{Safe(report.Snapshot.Id)}");
            text.AppendLine($"- 路径：{Safe(report.Snapshot.Path)}");
            text.AppendLine($"- 采集时间：{FormatTime(report.Snapshot.TakenAt)}");
            text.AppendLine($"- 应用版本 / 运行时版本：{Safe(report.Snapshot.AppVersion)} / {Safe(report.Snapshot.DshVersion)}");
            text.AppendLine($"- SHA256：{Safe(report.Snapshot.Sha256)}");
            text.AppendLine($"- 触发方式：{report.Snapshot.Trigger}");
            text.AppendLine($"- 冻结：{(report.Snapshot.Frozen ? "是（更新前快照，不会被自动清理）" : "否")}");
            text.AppendLine();
        }

        text.AppendLine($"## 问题（{report.Problems.Count}）");
        text.AppendLine();
        if (report.Problems.Count == 0)
        {
            text.AppendLine("无。");
        }
        else
        {
            var index = 1;
            foreach (var problem in report.Problems)
            {
                var line = $"{index}. [{SeverityLabel(problem.Severity)}] {Safe(problem.Code)} — {Safe(problem.Message)}";
                if (!string.IsNullOrWhiteSpace(problem.Subject)) line += $"（对象：{Safe(problem.Subject)}）";
                text.AppendLine(line);
                if (!string.IsNullOrWhiteSpace(problem.Detail))
                {
                    text.AppendLine($"   - {Safe(problem.Detail)}");
                }

                index++;
            }
        }

        text.AppendLine();
        text.AppendLine($"## 动作（{report.Actions.Count}）");
        text.AppendLine();
        if (report.Actions.Count == 0)
        {
            text.AppendLine("无。");
        }
        else
        {
            foreach (var action in report.Actions)
            {
                var line = $"- {(action.Applied ? "已应用" : "计划")} {Safe(action.Stage)}：{Safe(action.Description)}";
                if (!string.IsNullOrWhiteSpace(action.Target)) line += $"（目标：{Safe(action.Target)}）";
                text.AppendLine(line);
            }
        }

        text.AppendLine();
        text.AppendLine($"## 警告（{report.Warnings.Count}）");
        text.AppendLine();
        if (report.Warnings.Count == 0)
        {
            text.AppendLine("无。");
        }
        else
        {
            foreach (var warning in report.Warnings) text.AppendLine("- " + Safe(warning));
        }

        text.AppendLine();
        text.AppendLine("## 回滚命令");
        text.AppendLine();
        var rollbackCommands = report.RollbackCommands.Count > 0
            ? report.RollbackCommands
            : BuildDefaultRollbackCommands(report);
        if (rollbackCommands.Count == 0)
        {
            text.AppendLine("无。");
        }
        else
        {
            text.AppendLine("```powershell");
            foreach (var command in rollbackCommands) text.AppendLine(command);
            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine("## 报告路径");
        text.AppendLine();
        text.AppendLine($"- JSON：{Safe(jsonPath)}");
        text.AppendLine($"- Markdown：{Safe(markdownPath)}");
        text.AppendLine();
        text.AppendLine("> 本报告只包含路径、哈希、版本与结果；不包含 cordis.patch.yml 内容、环境变量或凭据。");
        return text.ToString();
    }

    private static List<string> BuildDefaultRollbackCommands(OperationReport report)
    {
        var commands = new List<string>();
        switch (report.Kind?.ToLowerInvariant())
        {
            case "restore":
                if (report.Snapshot is not null)
                {
                    commands.Add($"loom-dsh restore --dry-run --snapshot {report.Snapshot.Id}");
                    commands.Add($"loom-dsh restore --snapshot {report.Snapshot.Id} --force");
                }

                commands.Add("loom-dsh check");
                break;
            case "snapshot":
                commands.Add("loom-dsh status");
                commands.Add("loom-dsh check");
                break;
            default:
                commands.Add("loom-dsh report");
                commands.Add("loom-dsh check");
                break;
        }

        return commands;
    }

    private static string KindLabel(string? kind) => kind?.ToLowerInvariant() switch
    {
        "snapshot" => "快照",
        "check" => "兼容性检查",
        "restore" => "文件级还原",
        "doctor" => "环境体检",
        _ => "操作",
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

    private static string SeverityLabel(Severity severity) => severity switch
    {
        Severity.Error => "错误",
        Severity.Warning => "警告",
        Severity.Info => "提示",
        _ => severity.ToString(),
    };

    private static string EvidenceLabel(EvidenceLevel level) => level switch
    {
        EvidenceLevel.Strong => "强",
        EvidenceLevel.Medium => "中",
        EvidenceLevel.Weak => "弱",
        _ => "无",
    };

    private static string FormatTime(DateTimeOffset time) =>
        time == default
            ? "—"
            : time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>Single-line, redacted, and never a patch/environment dump.</summary>
    private static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var flattened = value.Replace('\r', ' ').Replace('\n', ' ');
        return Redactor.Redact(flattened);
    }
}
