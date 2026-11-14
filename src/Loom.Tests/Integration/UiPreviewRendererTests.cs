using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// R6：<c>ui render</c> 必须产出 12 张固定快照；这里直接验清单数量/唯一性，并在 STA 线程里调用
/// <c>UiPreviewRenderer.Render</c> 证明它确实写出非空 PNG（不启动 Application，不弹窗）。
/// </summary>
public sealed class UiPreviewRendererTests
{
    [Fact]
    public void SnapshotCatalogHasTwelveUniqueNamesIncludingCompatPage()
    {
        var (type, _) = UiBindingAuditTests.LoadLoomType("Loom.Services.UiSnapshotCatalog");
        var names = (System.Collections.IEnumerable)type
            .GetProperty("FileNames", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        var list = names.Cast<string>().ToList();

        Assert.Equal(12, list.Count);
        Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("settings-compat-dark.png", list);
        Assert.Contains("settings-compat-light.png", list);
        var expectedCount = (int)UiBindingAuditTests.LoadLoomType("Loom.Services.UiSnapshotCatalog").Type
            .GetField("ExpectedCount", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Equal(12, expectedCount);
    }

    [Fact]
    public void RendererWritesNonEmptyPngOnStaThread()
    {
        var directory = Path.Combine(Path.GetTempPath(), "loom-ui-render-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Exception? failure = null;
            string? outputPath = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var (type, _) = UiBindingAuditTests.LoadLoomType("Loom.Services.UiPreviewRenderer");
                    var render = type.GetMethod("Render", BindingFlags.Public | BindingFlags.Static);
                    Assert.NotNull(render);
                    var view = new Border
                    {
                        Width = 240,
                        Height = 120,
                        Background = Brushes.White,
                    };
                    outputPath = (string)render!.Invoke(
                        null,
                        new object?[] { view, Brushes.Black, Path.Combine(directory, "probe.png") })!;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF 渲染线程超时");
            if (failure is not null) throw new InvalidOperationException("UiPreviewRenderer.Render 失败", failure);

            Assert.NotNull(outputPath);
            Assert.True(File.Exists(outputPath));
            var bytes = File.ReadAllBytes(outputPath!);
            Assert.True(bytes.Length > 100, "PNG 不应为空：" + bytes.Length + " bytes");
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4));
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
