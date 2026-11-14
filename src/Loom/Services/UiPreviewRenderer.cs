using Size = System.Windows.Size;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// WPF/WinForms 同时启用时消除类型歧义
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Loom.Services;

/// <summary>
/// Headless preview renderer for <c>loom-dsh ui render</c>. Measures/arranges a UI page detached from
/// any window, then rasterises it at 2x with a 40px margin so card shadows are visible.
/// </summary>
public static class UiPreviewRenderer
{
    public const int Scale = 2;
    public const double Margin = 40;

    public static string Render(FrameworkElement view, Brush? background, string outputPath)
    {
        var width = view.Width;
        if (double.IsNaN(width) || width <= 0) width = 380;

        var height = view.Height;
        if (double.IsNaN(height) || height <= 0)
        {
            // Two passes: the first pass lets bindings/items panel finish their work, the second
            // gives the stable DesiredSize used to size the preview canvas.
            view.Measure(new Size(width, double.PositiveInfinity));
            view.Arrange(new Rect(0, 0, width, Math.Max(view.DesiredSize.Height, 1)));
            view.UpdateLayout();
            view.InvalidateMeasure();
            view.InvalidateArrange();
            view.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Ceiling(view.DesiredSize.Height);
        }

        var host = new Grid
        {
            Width = width + Margin * 2,
            Height = height + Margin * 2,
            Background = background ?? Brushes.Transparent,
        };
        view.Margin = new Thickness(Margin);
        host.Children.Add(view);

        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        var pixelWidth = (int)Math.Ceiling(host.Width * Scale);
        var pixelHeight = (int)Math.Ceiling(host.Height * Scale);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96.0 * Scale, 96.0 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using (var stream = File.Create(outputPath))
        {
            encoder.Save(stream);
        }

        // R6：产物必须是非空 PNG；空文件/编码失败时让 ui render 走失败退出码，而不是静默成功。
        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length < 8)
        {
            throw new InvalidOperationException("界面预览 PNG 生成失败（文件为空）：" + outputPath);
        }

        var signature = new byte[8];
        using (var read = File.OpenRead(outputPath))
        {
            if (read.Read(signature, 0, signature.Length) != signature.Length
                || signature[0] != 0x89 || signature[1] != 0x50 || signature[2] != 0x4E || signature[3] != 0x47)
            {
                throw new InvalidOperationException("界面预览 PNG 文件头无效：" + outputPath);
            }
        }

        return outputPath;
    }

}
