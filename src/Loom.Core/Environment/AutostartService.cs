using System.Text.RegularExpressions;
using Microsoft.Win32;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;

namespace Loom.Core.Environment;

/// <summary>
/// Single autostart mechanism: per-user <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// value "Loom for DSH". Command is always <c>"&lt;exe&gt;" --tray</c> (quoted full path).
/// All registry failures are logged and swallowed: autostart must never break the tool.
/// No scheduled task is created (avoids the double-start mix documented in the review).
/// </summary>
public sealed class AutostartService : IAutostartService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Loom for DSH";
    public const string TrayArgument = "--tray";

    private static readonly Regex FullyQuotedCommand = new("^\"[^\"]+\"(?:\\s+.*)?$", RegexOptions.Compiled);

    private readonly ILogger _logger;

    public AutostartService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Builds the Run value command: quoted executable path + --tray.</summary>
    public static string BuildCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("executable path is required", nameof(executablePath));
        }

        var path = executablePath.Trim().Trim('"');
        return $"\"{path}\" {TrayArgument}";
    }

    /// <summary>
    /// Normalizes a caller-supplied command so the executable part is always quoted.
    /// A command that already begins with a quoted executable is left untouched; for an
    /// unquoted command the executable ends at the .exe token (paths may contain spaces).
    /// </summary>
    public static string NormalizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var trimmed = command.Trim();
        if (FullyQuotedCommand.IsMatch(trimmed)) return trimmed;

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableEnd > 0)
        {
            executableEnd += ".exe".Length;
            var executable = trimmed[..executableEnd].Trim();
            var arguments = trimmed[executableEnd..].Trim();
            return arguments.Length == 0 ? $"\"{executable}\"" : $"\"{executable}\" {arguments}";
        }

        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex <= 0) return $"\"{trimmed}\"";
        var bareExecutable = trimmed[..spaceIndex].Trim();
        var bareArguments = trimmed[(spaceIndex + 1)..].Trim();
        return bareArguments.Length == 0 ? $"\"{bareExecutable}\"" : $"\"{bareExecutable}\" {bareArguments}";
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Trim().Length > 0;
        }
        catch (Exception exception)
        {
            _logger.Log(LogLevel.Warn, $"Cannot read autostart state: {exception.Message}");
            return false;
        }
    }

    public string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception exception)
        {
            _logger.Log(LogLevel.Warn, $"Cannot read autostart command: {exception.Message}");
            return null;
        }
    }

    public Task SetEnabledAsync(bool enabled, string command, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled)
            {
                var normalized = NormalizeCommand(command);
                if (normalized.Length == 0)
                {
                    _logger.Log(LogLevel.Warn, "Autostart enable skipped: empty command.");
                    return Task.CompletedTask;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null)
                {
                    _logger.Log(LogLevel.Warn, "Autostart enable failed: HKCU Run key unavailable.");
                    return Task.CompletedTask;
                }

                var current = key.GetValue(ValueName) as string;
                if (string.Equals(current, normalized, StringComparison.Ordinal))
                {
                    _logger.Log(LogLevel.Debug, "Autostart already registered with the same command.");
                    return Task.CompletedTask;
                }

                key.SetValue(ValueName, normalized, RegistryValueKind.String);
                _logger.Log(LogLevel.Info, $"Autostart enabled: {normalized}");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                var current = key?.GetValue(ValueName) as string;
                if (key is null || current is null)
                {
                    _logger.Log(LogLevel.Debug, "Autostart already disabled.");
                    return Task.CompletedTask;
                }

                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.Log(LogLevel.Info, "Autostart disabled.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Autostart is a convenience; never let it fail a config save or startup.
            _logger.LogException(exception, enabled ? "Autostart enable failed" : "Autostart disable failed");
        }

        return Task.CompletedTask;
    }
}
