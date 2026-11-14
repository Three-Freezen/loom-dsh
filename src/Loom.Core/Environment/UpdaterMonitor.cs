using System.Text.Json;
using System.Text.RegularExpressions;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Environment;

/// <summary>
/// Read-only probe of the electron-updater cache
/// (<c>%LOCALAPPDATA%\@deepseek-aidsh-desktop-updater</c>).
/// Signals (verified against electron-updater behaviour):
/// <list type="bullet">
/// <item><c>pending\temp-&lt;fileName&gt;</c> = download started (earliest usable heads-up).</item>
/// <item><c>pending\&lt;fileName&gt;</c> + <c>pending\update-info.json</c> ({fileName, sha512}) = ready.</item>
/// </list>
/// Candidates whose filename version is &lt;= the current app version are ignored
/// (e.g. the stale 0.2.2-rc.1 installer left on this machine while 0.2.2-rc.1 is installed).
/// Nothing is ever deleted or copied.
/// </summary>
public sealed class UpdaterMonitor : IUpdaterMonitor
{
    private const string PendingDirectory = "pending";
    private const string TempPrefix = "temp-";
    private const string UpdateInfoFile = "update-info.json";

    /// <summary>
    /// Version token of an electron-updater artifact name. The updater names files
    /// <c>&lt;product&gt;-&lt;version&gt;-&lt;platform&gt;-&lt;arch&gt;.&lt;ext&gt;</c>, and semver
    /// prerelease identifiers may contain hyphens, so the platform suffix is removed
    /// first and only dot-separated prerelease identifiers are accepted.
    /// </summary>
    private static readonly Regex FileNameVersion =
        new(@"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)*)", RegexOptions.Compiled);

    private static readonly Regex FileNamePlatformSuffix =
        new(@"[-._](?:win|windows|win32|mac|macos|darwin|linux|universal)[-._].*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger _logger;

    public UpdaterMonitor(ILogger logger)
    {
        _logger = logger;
    }

    public Task<PendingInstallerInfo?> FindPendingAsync(
        DshInstallation installation,
        string currentAppVersion,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveUpdaterRoot(installation);
        if (root is null) return Task.FromResult<PendingInstallerInfo?>(null);

        var pendingDir = Path.Combine(root, PendingDirectory);
        if (!Directory.Exists(pendingDir))
        {
            _logger.Log(LogLevel.Debug, $"Updater pending dir missing: {pendingDir}");
            return Task.FromResult<PendingInstallerInfo?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SemVersion? current = null;
        if (!string.IsNullOrWhiteSpace(currentAppVersion) && SemVersion.TryParse(currentAppVersion, out var parsed))
        {
            current = parsed;
        }

        var candidates = new List<PendingInstallerInfo>();
        try
        {
            // Ready: update-info.json names the final installer (sha512 included).
            var updateInfoPath = Path.Combine(pendingDir, UpdateInfoFile);
            if (File.Exists(updateInfoPath))
            {
                var info = ReadUpdateInfo(updateInfoPath);
                if (info is not null && IsVersionNewer(info.TargetVersion, current))
                {
                    var installerPath = Path.Combine(pendingDir, info.FileName);
                    candidates.Add(new PendingInstallerInfo
                    {
                        TargetVersion = info.TargetVersion,
                        FileName = info.FileName,
                        Sha512 = info.Sha512,
                        Path = File.Exists(installerPath) ? installerPath : null,
                        DetectedAt = SafeTimestamp(installerPath) ?? SafeTimestamp(updateInfoPath) ?? DateTimeOffset.UtcNow,
                        Stage = "ready",
                    });
                }
            }

            // Download in progress: electron-updater writes pending\temp-<fileName> first.
            foreach (var file in Directory.EnumerateFiles(pendingDir))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var targetName = fileName[TempPrefix.Length..];
                if (!TryVersionFromFileName(targetName, out var version)) continue;
                if (!IsVersionNewer(version.ToString(), current)) continue;

                candidates.Add(new PendingInstallerInfo
                {
                    TargetVersion = version.ToString(),
                    FileName = targetName,
                    Sha512 = null,
                    Path = file,
                    DetectedAt = SafeTimestamp(file) ?? DateTimeOffset.UtcNow,
                    Stage = "download",
                });
            }
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Updater cache probe failed");
            return Task.FromResult<PendingInstallerInfo?>(null);
        }

        if (candidates.Count == 0)
        {
            _logger.Log(LogLevel.Debug, "No pending DSH update newer than the installed version.");
            return Task.FromResult<PendingInstallerInfo?>(null);
        }

        // Highest version wins; a completed "ready" candidate beats an in-progress
        // download of the same version.
        var best = candidates
            .OrderByDescending(candidate => ParseVersion(candidate.TargetVersion))
            .ThenByDescending(candidate => candidate.Stage == "ready")
            .ThenByDescending(candidate => candidate.DetectedAt)
            .First();

        _logger.Log(LogLevel.Info, $"Pending DSH update: {best.TargetVersion} stage={best.Stage} file={best.FileName}");
        return Task.FromResult<PendingInstallerInfo?>(best);
    }

    private string? ResolveUpdaterRoot(DshInstallation installation)
    {
        if (!string.IsNullOrWhiteSpace(installation.UpdaterRoot)) return installation.UpdaterRoot;
        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, DshDefaults.UpdaterFolderName);
    }

    private sealed record UpdateInfo(string FileName, string? Sha512, string TargetVersion);

    private UpdateInfo? ReadUpdateInfo(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? fileName = null;
            string? sha512 = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("fileName") || property.NameEquals("filename"))
                {
                    fileName = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (property.NameEquals("sha512"))
                {
                    sha512 = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
            }

            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (!TryVersionFromFileName(fileName!, out var version)) return null;
            return new UpdateInfo(fileName!, sha512, version.ToString());
        }
        catch (Exception exception)
        {
            _logger.Log(LogLevel.Debug, $"Cannot read update-info.json: {exception.Message}");
            return null;
        }
    }

    private static bool IsVersionNewer(string targetVersion, SemVersion? current)
    {
        if (current is null) return true;
        return SemVersion.TryParse(targetVersion, out var target) && target > current;
    }

    private static DateTimeOffset? SafeTimestamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the semver token from an installer file name (see <see cref="FileNameVersion"/>).</summary>
    private static bool TryVersionFromFileName(string fileName, out SemVersion version)
    {
        version = new SemVersion();
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var normalized = FileNamePlatformSuffix.Replace(fileName, "");
        var match = FileNameVersion.Match(normalized);
        return match.Success && SemVersion.TryParse(match.Groups[1].Value, out version);
    }

    private static SemVersion ParseVersion(string text) =>
        SemVersion.TryParse(text, out var version) ? version : new SemVersion();
}
