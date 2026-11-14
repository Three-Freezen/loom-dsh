using System.IO;
using System.Diagnostics;

namespace Loom.Services;

/// <summary>Thin wrapper around ShellExecute for opening folders/files and copying paths.</summary>
public static class ShellLauncher
{
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // folder may be read-only or already gone; still try to open
        }

        Open(path);
    }

    /// <summary>Opens a folder (if it exists) or a file with the default application.</summary>
    public static void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // best effort: shell launch failures must not crash the tray
        }
    }

    /// <summary>Opens an http(s) URL with the default browser.</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch
        {
            // best effort: a broken default browser must not crash the settings window
        }
    }

    public static bool CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
