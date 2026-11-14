using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>
/// P2-2 回归：跨进程日志追加不能互相覆盖。
///
/// 旧实现长期持有一个 <c>FileMode.Append</c> 句柄（<c>FileShare.ReadWrite</c>）。两个写者（托盘进程 +
/// 直连 CLI 进程）各自记住打开时的文件位置，后写者会从陈旧偏移覆盖前者的行首——v0.1.1 想修的
/// “关键 ERROR 被吞”在跨进程仍然存在。这里用**两个独立的 FileLogger 实例**模拟两个进程
/// （各自有独立句柄，正是缺陷触发条件；沙箱不便稳定地起第二个 .NET 进程），并发各写 100 行、
/// 共 200 行，断言总行数正确、无重复、无截断。
/// </summary>
public sealed class FileLoggerCrossProcessTests
{
    [Fact]
    public async Task TwoIndependentHandlesWriting200LinesKeepEveryLine()
    {
        using var temp = new TempDir();
        using var first = new FileLogger(temp.Paths);
        using var second = new FileLogger(temp.Paths);

        const int linesPerWriter = 100; // 合计 200 行
        Task[] writers =
        {
            Task.Run(() =>
            {
                for (var index = 0; index < linesPerWriter; index++)
                {
                    first.Log(LogLevel.Info, $"A-{index:000}-" + new string('a', 40));
                }
            }),
            Task.Run(() =>
            {
                for (var index = 0; index < linesPerWriter; index++)
                {
                    second.Log(LogLevel.Info, $"B-{index:000}-" + new string('b', 40));
                }
            }),
        };
        await Task.WhenAll(writers);

        var lines = File.ReadAllLines(temp.Paths.LogFilePath, Encoding.UTF8);

        Assert.Equal(linesPerWriter * 2, lines.Length);                 // 没有被覆盖成 100 行
        Assert.Equal(linesPerWriter * 2, lines.Distinct().Count());     // 没有重复/错位
        Assert.Contains(lines, line => line.Contains("A-099", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("B-099", StringComparison.Ordinal));
        // 每一行都必须以时间戳开头、包含级别：证明没有被“截断成半行”。
        Assert.All(lines, line => Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[INFO\] ", line));
    }

    [Fact]
    public void RotateKeepsNewestFileAndAppendsStartFresh()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        // 构造 >5MB 的现有日志，触发下一次写入后的轮转。
        Directory.CreateDirectory(paths.LogsDir);
        File.WriteAllText(paths.LogFilePath, new string('x', 5 * 1024 * 1024 + 16));

        using (var logger = new FileLogger(paths))
        {
            logger.Log(LogLevel.Info, "after-rotate");
            Assert.True(File.Exists(paths.LogFilePath + ".1"), "超过 5MB 后应轮转为 loom.log.1");
            Assert.Contains("after-rotate", File.ReadAllText(paths.LogFilePath + ".1"));
            // 轮转后立刻有新的空日志文件，后续写入正常。
            logger.Log(LogLevel.Info, "after-rotate-second");
            Assert.Contains("after-rotate-second", File.ReadAllText(paths.LogFilePath));
        }
    }
}
