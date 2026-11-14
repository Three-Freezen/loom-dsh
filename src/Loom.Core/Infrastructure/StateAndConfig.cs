using Loom.Core.Abstractions;
using Loom.Core.Models;

namespace Loom.Core.Infrastructure;

/// <summary>Loads/saves config.json with schema validation and corrupt-file quarantine.</summary>
public static class ConfigStore
{
    public static LoomConfig Load(LoomPaths paths)
    {
        var config = AtomicFile.ReadJsonSafe<LoomConfig>(paths.ConfigFile) ?? new LoomConfig();
        Normalize(config);
        return config;
    }

    public static void Save(LoomPaths paths, LoomConfig config)
    {
        Normalize(config);
        AtomicFile.WriteJson(paths.ConfigFile, config);
    }

    public static void Normalize(LoomConfig config)
    {
        config.SchemaVersion = Math.Max(config.SchemaVersion, 1);
        if (config.KeepSnapshots < 1) config.KeepSnapshots = 1;
        if (config.KeepSnapshots > 50) config.KeepSnapshots = 50;
        if (config.MaxSnapshotBytes < 50L * 1024 * 1024) config.MaxSnapshotBytes = 50L * 1024 * 1024;
        if (config.MaxArchiveBytes < 10L * 1024 * 1024) config.MaxArchiveBytes = 10L * 1024 * 1024;
        if (config.OperationTimeoutSeconds < 5) config.OperationTimeoutSeconds = 5;
        if (config.OperationTimeoutSeconds > 900) config.OperationTimeoutSeconds = 900;
        if (string.IsNullOrWhiteSpace(config.Theme)) config.Theme = "system";
        if (config.Theme is not ("system" or "dark" or "light")) config.Theme = "system";
        if (string.IsNullOrWhiteSpace(config.LogLevel)) config.LogLevel = "info";
        if (string.IsNullOrWhiteSpace(config.AutoUpgradePolicy)) config.AutoUpgradePolicy = "A";
    }
}

/// <summary>Loads/saves state.json (tool state, snapshot pointers, pending update).</summary>
public static class StateStore
{
    public static LoomState Load(LoomPaths paths)
    {
        var state = AtomicFile.ReadJsonSafe<LoomState>(paths.StateFile) ?? new LoomState();
        if (state.SchemaVersion > 1)
        {
            AtomicFile.TryQuarantine(paths.StateFile);
            state = new LoomState();
        }

        state.SchemaVersion = 1;
        return state;
    }

    public static void Save(LoomPaths paths, LoomState state)
    {
        state.SchemaVersion = 1;
        state.UpdatedAt = DateTimeOffset.Now;
        AtomicFile.WriteJson(paths.StateFile, state);
    }
}

/// <summary>Cross-process gate: one Loom write operation at a time (named mutex + in-process semaphore).</summary>
public sealed class OperationGate : IOperationGate, IDisposable
{
    public const string MutexName = "Local\\LoomForDsh-Operation-v1";
    private readonly SemaphoreSlim _local = new(1, 1);

    public async Task<IDisposable> AcquireAsync(string operationKind, CancellationToken cancellationToken = default)
    {
        await _local.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutex = new Mutex(false, MutexName);
            while (true)
            {
                try
                {
                    if (mutex.WaitOne(200)) break;
                }
                catch (AbandonedMutexException)
                {
                    // previous Loom process crashed while holding the gate: take ownership
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return new Releaser(mutex, _local);
        }
        catch
        {
            _local.Release();
            throw;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly SemaphoreSlim _local;
        private bool _released;

        public Releaser(Mutex mutex, SemaphoreSlim local)
        {
            _mutex = mutex;
            _local = local;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _local.Release();
        }
    }

    public void Dispose() => _local.Dispose();
}

/// <summary>Semver subset sufficient for app-version comparisons and installer filenames.</summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; init; }
    public int Minor { get; init; }
    public int Patch { get; init; }
    public string? Prerelease { get; init; }
    public string? Build { get; init; }

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(
            text.Trim(),
            @"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$");
        if (!match.Success) return false;
        version = new SemVersion
        {
            Major = int.Parse(match.Groups[1].Value),
            Minor = int.Parse(match.Groups[2].Value),
            Patch = int.Parse(match.Groups[3].Value),
            Prerelease = match.Groups[4].Success ? match.Groups[4].Value : null,
            Build = match.Groups[5].Success ? match.Groups[5].Value : null,
        };
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Invalid semver: {text}");

    public static bool TryParseFromFileName(string fileName, out SemVersion version)
    {
        version = new SemVersion();
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // Updater file names look like deepseek-harness-<version>-win-x64.exe. Strip the platform
        // and archive suffixes first so the prerelease identifiers ("rc.1") do not swallow them.
        var candidate = System.Text.RegularExpressions.Regex.Replace(
            fileName,
            @"-(win32|win64|win|linux|darwin|mac|osx|arm64|x64|ia32|armv7l?)[-.].*$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        candidate = System.Text.RegularExpressions.Regex.Replace(
            candidate,
            @"\.(exe|msi|zip|7z|blockmap|dmg|deb|rpm|appimage)$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = System.Text.RegularExpressions.Regex.Match(
            candidate,
            @"(\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)");
        return match.Success && TryParse(match.Groups[1].Value, out version);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            if (index >= left.Length) return -1;
            if (index >= right.Length) return 1;
            var leftNumeric = int.TryParse(left[index], out var leftNumber);
            var rightNumeric = int.TryParse(right[index], out var rightNumber);
            int cmp;
            if (leftNumeric && rightNumeric) cmp = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) cmp = -1;
            else if (rightNumeric) cmp = 1;
            else cmp = string.CompareOrdinal(left[index], right[index]);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    public bool Equals(SemVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease)) text += "-" + Prerelease;
        if (!string.IsNullOrEmpty(Build)) text += "+" + Build;
        return text;
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
}
