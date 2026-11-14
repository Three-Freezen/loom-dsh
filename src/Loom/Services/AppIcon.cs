using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// WPF/WinForms 同时启用时消除类型歧义
using ImageSource = System.Windows.Media.ImageSource;

namespace Loom.Services;

/// <summary>
/// Window/taskbar icon provider. Resolves the theme-appropriate LOOM ico
/// (<c>Loom.Icons.dark.ico</c> / <c>Loom.Icons.light.ico</c>, the same assets the tray uses),
/// decodes a PNG frame from the ICO directory ourselves (GDI+ cannot render PNG-compressed
/// frames) and caches the resulting frozen <see cref="BitmapSource"/>.
///
/// The ico assets are generated from the same SVG as <c>Views/LogoMark.xaml</c>, so in-app
/// brand mark, window icons and the notification-area icon stay visually identical.
/// </summary>
public static class AppIcon
{
    private const int WindowIconSize = 64;
    private const int TrayIconSize = 32;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.Ordinal);

    /// <summary>Theme-appropriate icon for <paramref name="theme"/> ("dark" | "light" | system).</summary>
    public static ImageSource? For(string? theme) => Frame(theme, WindowIconSize);

    /// <summary>Icon for the currently applied theme; null when the ico assets cannot be read.</summary>
    public static ImageSource? Current => For(ThemeManager.EffectiveTheme);

    /// <summary>Icon for the notification area (32px) — used by previews to compare with LogoMark.</summary>
    public static ImageSource? Tray(string? theme) => Frame(theme, TrayIconSize);

    /// <summary>
    /// Decodes the ICO frame closest to <paramref name="size"/> (preferring the exact PNG frame).
    /// Returns a frozen bitmap so it can safely be handed to <see cref="System.Windows.Window.Icon"/>.
    /// </summary>
    public static BitmapSource? Frame(string? theme, int size)
    {
        var key = NormalizeTheme(theme);
        var cacheKey = key + ":" + size;
        lock (Gate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        BitmapSource? result = null;
        try
        {
            var bytes = LoadIconBytes(key);
            if (bytes is not null) result = DecodeFrame(bytes, size);
        }
        catch
        {
            // best effort: a broken icon must not break window creation
        }

        if (result is not null && result.CanFreeze) result.Freeze();
        lock (Gate)
        {
            Cache[cacheKey] = result;
        }

        return result;
    }

    private static string NormalizeTheme(string? theme)
        => string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";

    private static byte[]? LoadIconBytes(string theme)
    {
        var logicalName = theme == "dark" ? "Loom.Icons.dark.ico" : "Loom.Icons.light.ico";
        var fileName = theme == "dark" ? "loom-check-dark.ico" : "loom-check-light.ico";

        try
        {
            var stream = AppServices.OpenEmbedded(logicalName);
            if (stream is not null)
            {
                using (stream)
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }
        catch
        {
            // fall through to loose file
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the best PNG frame from an ICO byte array and decodes it with WPF.</summary>
    private static BitmapSource? DecodeFrame(byte[] bytes, int preferredSize)
    {
        if (bytes.Length < 6) return null;
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0) return null;

        var bestOffset = -1;
        var bestLength = -1;
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
                bestLength = length;
            }
        }

        if (bestOffset < 0) return null;

        var isPng = bestLength > 8
                    && bytes[bestOffset] == 0x89
                    && bytes[bestOffset + 1] == 0x50
                    && bytes[bestOffset + 2] == 0x4E
                    && bytes[bestOffset + 3] == 0x47;

        if (isPng)
        {
            using var png = new MemoryStream(bytes, bestOffset, bestLength, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = png;
            bitmap.EndInit();
            return bitmap;
        }

        // Fallback for non-PNG frames: let WPF's ICO decoder pick a frame.
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames
                .OrderBy(candidate => Math.Abs(candidate.PixelWidth - preferredSize))
                .First();
            return frame;
        }
        catch
        {
            return null;
        }
    }
}
