using System.Windows;
using Microsoft.Win32;

// WPF/WinForms 同时启用时消除类型歧义
using Application = System.Windows.Application;

namespace Loom.Services;

/// <summary>
/// Applies the dark/light LOOM resource dictionaries and resolves the system theme.
/// Theme dictionaries carry only token brushes; <c>Themes/Controls.xaml</c> carries styles and
/// references tokens with <c>DynamicResource</c>, so swapping the theme dictionary is enough to
/// re-skin every open window.
/// </summary>
public static class ThemeManager
{
    private const string ThemeDictionaryMarker = "/Themes/";

    private static readonly object Gate = new();
    private static ResourceDictionary? _themeDictionary;
    private static bool _initialized;

    /// <summary>Raised (on the UI thread) after a theme switch. Payload is "dark" or "light".</summary>
    public static event EventHandler<string>? ThemeChanged;

    /// <summary>Config value as stored in <c>LoomConfig.Theme</c>: system | dark | light.</summary>
    public static string RequestedTheme { get; private set; } = "system";

    /// <summary>Resolved theme actually in effect: dark | light.</summary>
    public static string EffectiveTheme { get; private set; } = "dark";

    public static bool IsDark => EffectiveTheme == "dark";

    /// <summary>Reads the Windows app theme from HKCU Personalize AppsUseLightTheme.</summary>
    public static string DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int number && number == 0 ? "dark" : "light";
        }
        catch
        {
            return "light";
        }
    }

    public static string Resolve(string? theme)
    {
        var requested = Normalize(theme);
        return requested switch
        {
            "dark" => "dark",
            "light" => "light",
            _ => DetectSystemTheme() == "dark" ? "dark" : "light",
        };
    }

    public static string Normalize(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return "system";
        return theme.Trim().ToLowerInvariant() switch
        {
            "dark" => "dark",
            "light" => "light",
            _ => "system",
        };
    }

    /// <summary>Applies a theme (system | dark | light). Safe to call from any thread.</summary>
    public static void Apply(string? theme)
    {
        var requested = Normalize(theme);
        var effective = Resolve(requested);
        var app = Application.Current;
        if (app is null)
        {
            RequestedTheme = requested;
            EffectiveTheme = effective;
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyCore(app, requested, effective));
            return;
        }

        ApplyCore(app, requested, effective);
    }

    private static void ApplyCore(Application app, string requested, string effective)
    {
        lock (Gate)
        {
            var resources = app.Resources;

            EnsureDictionary(resources, "Themes/Controls.xaml", insertFirst: true);

            var replacement = LoadDictionary($"Themes/{(effective == "dark" ? "Dark" : "Light")}.xaml");
            if (_themeDictionary is null)
            {
                resources.MergedDictionaries.Insert(0, replacement);
            }
            else
            {
                var index = resources.MergedDictionaries.IndexOf(_themeDictionary);
                if (index >= 0)
                {
                    resources.MergedDictionaries[index] = replacement;
                }
                else
                {
                    resources.MergedDictionaries.Insert(0, replacement);
                }
            }

            _themeDictionary = replacement;
            RequestedTheme = requested;
            EffectiveTheme = effective;
            _initialized = true;
        }

        try
        {
            ThemeChanged?.Invoke(null, effective);
        }
        catch
        {
            // best effort; a broken subscriber must not break theming
        }
    }

    private static void EnsureDictionary(ResourceDictionary resources, string relativePath, bool insertFirst)
    {
        foreach (var dictionary in resources.MergedDictionaries)
        {
            var source = dictionary.Source?.OriginalString;
            if (source is not null && source.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase))
            {
                if (insertFirst && resources.MergedDictionaries.IndexOf(dictionary) != 0)
                {
                    resources.MergedDictionaries.Remove(dictionary);
                    resources.MergedDictionaries.Insert(0, dictionary);
                }

                return;
            }
        }

        var loaded = LoadDictionary(relativePath);
        if (insertFirst)
        {
            resources.MergedDictionaries.Insert(0, loaded);
        }
        else
        {
            resources.MergedDictionaries.Add(loaded);
        }
    }

    private static ResourceDictionary LoadDictionary(string relativePath)
    {
        var assembly = typeof(ThemeManager).Assembly.GetName().Name;
        var uri = new Uri($"/{assembly};component/{relativePath}", UriKind.Relative);
        return new ResourceDictionary { Source = uri };
    }

    /// <summary>True when <see cref="Apply"/> has run at least once in this process.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Resolves a token brush from application resources (null when not initialised).</summary>
    public static System.Windows.Media.Brush? Brush(string key)
    {
        try
        {
            return Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;
        }
        catch
        {
            return null;
        }
    }
}
