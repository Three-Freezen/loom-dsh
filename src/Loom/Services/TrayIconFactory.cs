using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Loom.Core.Models;

namespace Loom.Services;

/// <summary>
/// Builds the notification-area icon: a base light/dark LOOM ico plus a small status dot
/// (待退出/需人工 黄、失败 红、未找到 灰、处理中 蓝)。
///
/// The shipped .ico uses PNG-compressed frames, which <see cref="System.Drawing.Icon"/> cannot render
/// reliably (GDI+ produces garbage for PNG frames). We therefore decode the ICO directory ourselves,
/// pick the 32px PNG frame and let GDI+ decode it, which preserves the alpha channel.
/// HICON handles are owned here and destroyed explicitly to avoid GDI handle leaks.
/// </summary>
internal static class TrayIconFactory
{
    private const int IconSize = 32;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly object Gate = new();
    private static readonly List<MemoryStream> EmbeddedStreams = new();

    /// <summary>Creates a status overlay icon; the returned handle must be released with <see cref="Destroy"/>.</summary>
    public static (System.Drawing.Icon Icon, IntPtr Handle) Create(TrayStatus state, bool darkTaskbar)
    {
        using var bitmap = CreateBitmap(state, darkTaskbar);
        var handle = bitmap.GetHicon();
        return (System.Drawing.Icon.FromHandle(handle), handle);
    }

    /// <summary>Builds the composited 32x32 bitmap (base ico + status dot) for the given state.</summary>
    internal static Bitmap CreateBitmap(TrayStatus state, bool darkTaskbar)
    {
        var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var baseBitmap = LoadBaseBitmap(darkTaskbar, IconSize);
            if (baseBitmap is not null)
            {
                graphics.DrawImage(baseBitmap, new Rectangle(0, 0, IconSize, IconSize));
            }

            var dotColor = DotColor(state);
            if (dotColor.HasValue)
            {
                // 圆点 + 与任务栏同色的描边环，保证在深/浅任务栏上都清晰
                var ring = darkTaskbar ? Color.FromArgb(255, 27, 27, 28) : Color.FromArgb(255, 245, 246, 248);
                using var ringBrush = new SolidBrush(ring);
                using var dotBrush = new SolidBrush(dotColor.Value);
                graphics.FillEllipse(ringBrush, 16.5f, 16.5f, 15f, 15f);
                graphics.FillEllipse(dotBrush, 19f, 19f, 10f, 10f);
            }
        }

        return bitmap;
    }

    private static Bitmap? LoadBaseBitmap(bool darkTaskbar, int size)
    {
        var logicalName = darkTaskbar ? "Loom.Icons.dark.ico" : "Loom.Icons.light.ico";
        var fileName = darkTaskbar ? "loom-check-dark.ico" : "loom-check-light.ico";

        try
        {
            var stream = AppServices.OpenEmbedded(logicalName);
            if (stream is not null)
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                lock (Gate) EmbeddedStreams.Add(copy);
                return DecodeIcoFrame(copy, size);
            }
        }
        catch
        {
            // fall through to loose file
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName);
            if (File.Exists(path))
            {
                using var file = File.OpenRead(path);
                return DecodeIcoFrame(file, size);
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>Extracts the best PNG-compressed frame from an ICO stream and decodes it with GDI+.</summary>
    private static Bitmap? DecodeIcoFrame(Stream stream, int preferredSize)
    {
        var bytes = ReadAll(stream);
        if (bytes.Length < 6) return null;

        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0) return null;

        var bestOffset = -1;
        var bestSize = -1;
        var bestDistance = int.MaxValue;

        for (var index = 0; index < count; index++)
        {
            var entry = 6 + index * 16;
            if (entry + 16 > bytes.Length) break;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
            var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
            if (length <= 0 || offset <= 0 || offset + length > bytes.Length) continue;

            var distance = Math.Abs(width - preferredSize);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestOffset = offset;
                bestSize = length;
            }
        }

        if (bestOffset < 0) return null;

        var isPng = bestSize > 8
                    && bytes[bestOffset] == 0x89
                    && bytes[bestOffset + 1] == 0x50
                    && bytes[bestOffset + 2] == 0x4E
                    && bytes[bestOffset + 3] == 0x47;

        if (!isPng) return null;

        using var pngStream = new MemoryStream(bytes, bestOffset, bestSize, writable: false);
        using var image = Image.FromStream(pngStream);
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        return bitmap;
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory)
        {
            return memory.ToArray();
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Color? DotColor(TrayStatus state) => state switch
    {
        TrayStatus.Normal => null,
        TrayStatus.Busy => Color.FromArgb(210, 122, 170, 255),
        TrayStatus.AwaitingExit => Color.FromArgb(255, 245, 158, 11),
        TrayStatus.NeedsManual => Color.FromArgb(255, 245, 158, 11),
        TrayStatus.Failed => Color.FromArgb(255, 242, 90, 90),
        TrayStatus.NotFound => Color.FromArgb(255, 129, 133, 140),
        _ => null,
    };

    public static void Destroy(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            DestroyIcon(handle);
        }
        catch
        {
            // best effort
        }
    }
}
