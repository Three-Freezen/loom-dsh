using System.Text;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Reporting;
using Xunit;

namespace Loom.Tests.CoreFlows;

public sealed class ReportServiceTests
{
    private static LoomPaths PathsFor(string root) => new(Path.Combine(root, "loom-data"));

    private static OperationReport Report(string operationId, string summary, string? problemMessage = null) => new()
    {
        OperationId = operationId,
        Kind = "restore",
        StartedAt = DateTimeOffset.Now.AddSeconds(-5),
        FinishedAt = DateTimeOffset.Now,
        Outcome = OperationOutcome.Succeeded,
        Summary = summary,
        AppVersion = "0.2.2-rc.1",
        DshVersion = "0.2.2-rc.1",
        Evidence = EvidenceLevel.Strong,
        Snapshot = new SnapshotRef
        {
            Id = "20261114-120000-0.2.2-rc.1-0.2.2-rc.1",
            Path = @"C:\data\snapshots\20261114-120000",
            Sha256 = new string('a', 64),
            TakenAt = DateTimeOffset.Now.AddMinutes(-10),
            AppVersion = "0.2.2-rc.1",
            DshVersion = "0.2.2-rc.1",
            Frozen = true,
            PluginCount = 3,
            BundleCount = 5,
        },
        Problems =
        {
            new ReportProblem
            {
                Severity = Severity.Warning,
                Code = "W_PATCH_RESET",
                Message = problemMessage ?? "cordis.patch.yml 被重置",
                Subject = "cordis.patch.yml",
            },
        },
        Actions =
        {
            new ReportAction
            {
                Stage = "MergeBundles",
                Description = "按快照顺序插回 2 个 bundle",
                Target = "package.json",
                Applied = true,
            },
        },
        Warnings = { "存在需人工确认的插件。" },
    };

    [Fact]
    public async Task WritesJsonAndMarkdownWithUtf8NoBom()
    {
        using var sandbox = new LoomSandbox();
        var paths = PathsFor(sandbox.Root);
        var service = new ReportService();

        var view = await service.WriteAsync(Report("20261114-120000-restore-abc123", "已还原 2 项"), paths);

        Assert.NotNull(view.JsonPath);
        Assert.NotNull(view.MarkdownPath);
        Assert.True(File.Exists(view.JsonPath));
        Assert.True(File.Exists(view.MarkdownPath));
        Assert.EndsWith("report-20261114-120000-restore-abc123.json", view.JsonPath);
        Assert.EndsWith("report-20261114-120000-restore-abc123.md", view.MarkdownPath);

        var bytes = File.ReadAllBytes(view.JsonPath!);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("restore", json);

        var markdown = File.ReadAllText(view.MarkdownPath!, Encoding.UTF8);
        Assert.Contains("# Loom for DSH 操作报告", markdown);
        Assert.Contains("文件级还原", markdown);
        Assert.Contains("证据等级：强", markdown);
        Assert.Contains("W_PATCH_RESET", markdown);
        Assert.Contains("回滚命令", markdown);
        Assert.Contains("报告路径", markdown);
        Assert.Contains("快照", markdown);
        Assert.Contains("loom-dsh restore", markdown);
        Assert.Contains("不包含 cordis.patch.yml 内容", markdown);
    }

    [Fact]
    public async Task MarkdownRedactsCredentials()
    {
        using var sandbox = new LoomSandbox();
        var paths = PathsFor(sandbox.Root);
        var service = new ReportService();

        var view = await service.WriteAsync(
            Report("op-secret", "凭据检查", "npm config _authToken=super-secret-token"),
            paths);

        var markdown = File.ReadAllText(view.MarkdownPath!, Encoding.UTF8);
        Assert.DoesNotContain("super-secret-token", markdown);
        Assert.Contains("***", markdown);
    }

    [Fact]
    public async Task ReadLatestReturnsNewestReportAndItsMarkdown()
    {
        using var sandbox = new LoomSandbox();
        var paths = PathsFor(sandbox.Root);
        var service = new ReportService();

        await service.WriteAsync(Report("first", "第一次报告"), paths);
        await Task.Delay(40);
        var latest = await service.WriteAsync(Report("second", "第二次报告"), paths);

        var read = await service.ReadLatestAsync(paths);

        Assert.NotNull(read);
        Assert.Equal(latest.JsonPath, read!.JsonPath);
        Assert.Contains("第二次报告", read.Markdown);
        // JSON keeps Chinese escaped (System.Text.Json default encoder), so compare the deserialised summary.
        var parsed = JsonDefaults.Deserialize<OperationReport>(read.Json);
        Assert.Equal("第二次报告", parsed!.Summary);
    }

    [Fact]
    public async Task ReadAsyncResolvesSiblingFilesAndReconstructsMarkdown()
    {
        using var sandbox = new LoomSandbox();
        var paths = PathsFor(sandbox.Root);
        var service = new ReportService();
        var view = await service.WriteAsync(Report("op-read", "读取测试"), paths);

        var fromJson = await service.ReadAsync(view.JsonPath!);
        Assert.NotNull(fromJson);
        Assert.Contains("读取测试", fromJson!.Markdown);

        File.Delete(view.MarkdownPath!);
        var reconstructed = await service.ReadAsync(view.JsonPath!);
        Assert.NotNull(reconstructed);
        Assert.Contains("读取测试", reconstructed!.Markdown);

        Assert.Null(await service.ReadAsync(Path.Combine(sandbox.Root, "missing-report.json")));
    }
}
