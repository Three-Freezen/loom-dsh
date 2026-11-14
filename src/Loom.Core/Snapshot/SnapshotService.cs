using System.Globalization;
using System.Text.Json;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;

namespace Loom.Core.Snapshot;

/// <summary>
/// Captures the desktop profile into <c>snapshots\&lt;id&gt;</c> (light: files + metadata,
/// full: additionally a node_modules copy bounded by config limits) and applies the retention policy
/// of design 9.1-9.4.
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    private static readonly string[] CoreProfileFiles =
    {
        DshDefaults.ManifestFile,
        DshDefaults.LockfileFile,
        DshDefaults.PatchFile,
        DshDefaults.CompatibilityFile,
        DshDefaults.CordisFile,
    };

    private readonly LoomPaths _paths;
    private readonly ILogger _logger;
    private readonly List<string> _lastWarnings = new();

    public SnapshotService(LoomPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Warnings produced by the most recent WriteAsync (e.g. a full archive downgraded to light).</summary>
    public IReadOnlyList<string> LastWarnings
    {
        get
        {
            lock (_lastWarnings) return _lastWarnings.ToList();
        }
    }

    public Task<SnapshotManifest> CaptureAsync(
        DshInstallation installation,
        SnapshotTrigger trigger,
        bool full,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (installation is null) throw new ArgumentNullException(nameof(installation));

        var profile = new ProfileReader(_logger).Read(installation, strict: true);
        if (!profile.HasManifest)
        {
            throw new LoomException(
                LoomErrorCodes.NotFound,
                $"profile 缺少 package.json，无法采集快照：{installation.ProfileDir}");
        }

        var manifest = new SnapshotManifest
        {
            Id = BuildSnapshotId(installation, DateTimeOffset.Now),
            TakenAt = DateTimeOffset.Now,
            Trigger = trigger,
            AppVersion = string.IsNullOrWhiteSpace(installation.AppVersion) ? "unknown" : installation.AppVersion,
            DshVersion = string.IsNullOrWhiteSpace(installation.DshVersion) ? "unknown" : installation.DshVersion,
            InstallDir = installation.InstallDir ?? "",
            ProfileDir = installation.ProfileDir,
            DshHome = installation.DshHome ?? "",
            Environment = new SnapshotEnvironment
            {
                Platform = "win32",
                Arch = System.Environment.Is64BitProcess ? "x64" : "x86",
                ToolVersion = LoomPaths.ToolVersion,
                ElectronVersion = null,
            },
        };

        manifest.Plugins = profile.Plugins
            .Select(ClonePlugin)
            .ToList();

        var pluginsByName = manifest.Plugins
            .GroupBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in profile.Bundles)
        {
            var bundleClass = IsInstallBaseline(installation, name)
                ? BundleClass.Baseline
                : DshDefaults.ClassifyBundle(name);
            pluginsByName.TryGetValue(name, out var plugin);
            manifest.Profile.Bundles.Add(new BundleSnapshot
            {
                Name = name,
                Class = bundleClass,
                Version = plugin?.Version,
                ManifestPath = plugin is null || string.IsNullOrEmpty(plugin.Dir)
                    ? null
                    : Path.Combine(plugin.Dir, "package.json"),
            });
        }

        manifest.Profile.Dependencies = profile.Dependencies.ToDictionary(
            pair => pair.Key,
            pair => new DependencySnapshot
            {
                Spec = pair.Value.Spec,
                Resolved = pair.Value.Resolved,
                Integrity = pair.Value.Integrity,
            },
            StringComparer.Ordinal);

        manifest.Profile.Patch = new PatchSnapshot
        {
            Sha256 = profile.PatchSha256,
            File = DshDefaults.PatchFile,
            Backups = profile.PatchBackups.ToList(),
        };

        var compatibilityPath = Path.Combine(installation.ProfileDir, DshDefaults.CompatibilityFile);
        var compatibility = ProfileFiles.ReadCompatibility(compatibilityPath);
        manifest.Profile.Compatibility = new CompatibilitySnapshot
        {
            File = File.Exists(compatibilityPath) ? DshDefaults.CompatibilityFile : null,
            Sha256 = compatibility.Sha256,
            Exemptions = compatibility.Exemptions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.Ordinal),
        };

        manifest.Profile.LockfileSha256 = profile.LockfileSha256;
        manifest.Profile.FileSha256 = profile.FileSha256.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        manifest.Files = new SnapshotFiles
        {
            PackageJson = File.Exists(Path.Combine(installation.ProfileDir, DshDefaults.ManifestFile))
                ? DshDefaults.ManifestFile
                : null,
            PnpmLock = File.Exists(Path.Combine(installation.ProfileDir, DshDefaults.LockfileFile))
                ? DshDefaults.LockfileFile
                : null,
            CordisPatch = File.Exists(Path.Combine(installation.ProfileDir, DshDefaults.PatchFile))
                ? DshDefaults.PatchFile
                : null,
            Compatibility = manifest.Profile.Compatibility.File,
            CordisYml = File.Exists(Path.Combine(installation.ProfileDir, DshDefaults.CordisFile))
                ? DshDefaults.CordisFile
                : null,
        };

        // The archive copy itself happens in WriteAsync because only that call receives LoomConfig
        // (MaxArchiveBytes) and the snapshot root used for the disk-space check.
        manifest.PackageArchive = new PackageArchive { Kind = full ? "node-modules-tree" : "none" };

        _logger.Log(
            LogLevel.Info,
            $"Captured profile {installation.ProfileDir}: bundles={manifest.Profile.Bundles.Count} " +
            $"plugins={manifest.Plugins.Count} full={full} trigger={trigger}");

        return Task.FromResult(manifest);
    }

    public Task<SnapshotRef> WriteAsync(
        SnapshotManifest manifest,
        LoomPaths paths,
        LoomConfig config,
        CancellationToken cancellationToken = default)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        config ??= new LoomConfig();
        ConfigStore.Normalize(config);
        paths.EnsureDirectories();

        lock (_lastWarnings) _lastWarnings.Clear();

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            manifest.Id = BuildSnapshotId(
                new DshInstallation { AppVersion = manifest.AppVersion, DshVersion = manifest.DshVersion },
                manifest.TakenAt == default ? DateTimeOffset.Now : manifest.TakenAt);
        }

        var id = LoomPaths.SanitizeFileName(manifest.Id);
        id = MakeUniqueSnapshotId(paths, id);
        manifest.Id = id;

        var snapshotDir = paths.SnapshotDir(id);
        var profileDir = Path.Combine(snapshotDir, "profile");
        Directory.CreateDirectory(profileDir);

        // 1. Copy the profile configuration files byte-for-byte.
        foreach (var fileName in CoreProfileFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(manifest.ProfileDir, fileName);
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(profileDir, fileName);
            File.Copy(source, destination, overwrite: true);
            RememberFile(manifest, fileName);
        }

        // 2. Optional full archive: copy the node_modules tree, bounded by config + disk space.
        if (string.Equals(manifest.PackageArchive?.Kind, "node-modules-tree", StringComparison.OrdinalIgnoreCase))
        {
            manifest.PackageArchive = CopyPackageArchive(manifest, snapshotDir, config, cancellationToken);
        }

        // 3. Write snapshot.json (UTF-8, no BOM) and hash it.
        var manifestPath = Path.Combine(snapshotDir, "snapshot.json");
        AtomicFile.WriteJson(manifestPath, manifest);
        var sha256 = Hashing.Sha256Hex(manifestPath);

        var reference = new SnapshotRef
        {
            Id = id,
            Path = snapshotDir,
            TakenAt = manifest.TakenAt == default ? DateTimeOffset.Now : manifest.TakenAt,
            AppVersion = manifest.AppVersion,
            DshVersion = manifest.DshVersion,
            Sha256 = sha256,
            Trigger = manifest.Trigger,
            SizeBytes = FileSystemUtil.DirectorySize(snapshotDir),
            Frozen = manifest.Trigger == SnapshotTrigger.PreUpdate,
            PluginCount = manifest.Plugins.Count,
            BundleCount = manifest.Profile.Bundles.Count,
        };

        _logger.Log(
            LogLevel.Info,
            $"Snapshot written: {id} ({reference.SizeBytes} bytes, archive={manifest.PackageArchive?.Kind})");
        return Task.FromResult(reference);
    }

    public Task<SnapshotManifest?> LoadAsync(SnapshotRef reference, CancellationToken cancellationToken = default)
    {
        if (reference is null) return Task.FromResult<SnapshotManifest?>(null);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = !string.IsNullOrWhiteSpace(reference.Path)
            ? reference.Path
            : _paths.SnapshotDir(reference.Id);
        var manifestPath = Path.Combine(directory, "snapshot.json");
        if (!File.Exists(manifestPath))
        {
            _logger.Log(LogLevel.Warn, $"Snapshot manifest missing: {manifestPath}");
            return Task.FromResult<SnapshotManifest?>(null);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(reference.Sha256))
            {
                var actual = Hashing.Sha256Hex(manifestPath);
                if (!string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log(LogLevel.Warn, $"Snapshot hash mismatch for {reference.Id} (expected {reference.Sha256}, got {actual})");
                    return Task.FromResult<SnapshotManifest?>(null);
                }
            }

            var manifest = AtomicFile.ReadJson<SnapshotManifest>(manifestPath);
            if (manifest is null) return Task.FromResult<SnapshotManifest?>(null);
            if (string.IsNullOrWhiteSpace(manifest.Id)) manifest.Id = reference.Id;
            if (string.IsNullOrWhiteSpace(manifest.ProfileDir)) manifest.ProfileDir = directory;
            return Task.FromResult<SnapshotManifest?>(manifest);
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, $"Failed to load snapshot {reference.Id}");
            return Task.FromResult<SnapshotManifest?>(null);
        }
    }

    public Task<int> RetainAsync(
        LoomPaths paths,
        LoomState state,
        LoomConfig config,
        CancellationToken cancellationToken = default)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        state ??= new LoomState();
        config ??= new LoomConfig();

        if (!Directory.Exists(paths.SnapshotsDir)) return Task.FromResult(0);

        var entries = new List<RetentionEntry>();
        foreach (var directory in Directory.EnumerateDirectories(paths.SnapshotsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.StartsWith("corrupt-", StringComparison.OrdinalIgnoreCase)) continue;

            var manifestPath = Path.Combine(directory, "snapshot.json");
            SnapshotManifest? manifest = null;
            if (File.Exists(manifestPath))
            {
                try
                {
                    manifest = AtomicFile.ReadJson<SnapshotManifest>(manifestPath);
                }
                catch (Exception exception)
                {
                    _logger.LogException(exception, "Retention: unreadable snapshot manifest " + directory);
                }
            }

            if (manifest is null)
            {
                // Design 9.4: a snapshot that failed verification is moved aside, never used as baseline.
                var corrupt = Path.Combine(paths.SnapshotsDir, "corrupt-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + name);
                FileSystemUtil.MoveDirectoryBestEffort(directory, corrupt);
                _logger.Log(LogLevel.Warn, $"Retention: moved corrupt snapshot {name} to {corrupt}");
                continue;
            }

            entries.Add(new RetentionEntry
            {
                Id = string.IsNullOrWhiteSpace(manifest.Id) ? name : manifest.Id,
                Directory = directory,
                TakenAt = manifest.TakenAt == default ? DateTimeOffset.MinValue : manifest.TakenAt,
                DshVersion = string.IsNullOrWhiteSpace(manifest.DshVersion) ? "unknown" : manifest.DshVersion,
                Frozen = manifest.Trigger == SnapshotTrigger.PreUpdate,
                SizeBytes = FileSystemUtil.DirectorySize(directory),
            });
        }

        var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(state.PreUpdateSnapshot?.Id)) protectedIds.Add(state.PreUpdateSnapshot!.Id);
        if (!string.IsNullOrWhiteSpace(state.LastSnapshot?.Id)) protectedIds.Add(state.LastSnapshot!.Id);
        if (!string.IsNullOrWhiteSpace(state.PendingRestoreSnapshotId)) protectedIds.Add(state.PendingRestoreSnapshotId!);

        var newestFirst = entries
            .OrderByDescending(entry => entry.TakenAt)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in newestFirst.Take(config.KeepSnapshots))
        {
            keep.Add(entry.Id);
        }

        // At least one snapshot per DSH version (the newest of each version).
        foreach (var group in newestFirst.GroupBy(entry => entry.DshVersion, StringComparer.OrdinalIgnoreCase))
        {
            keep.Add(group.First().Id);
        }

        foreach (var entry in entries)
        {
            if (entry.Frozen) keep.Add(entry.Id);
        }

        foreach (var id in protectedIds)
        {
            keep.Add(id);
        }

        var removed = 0;
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = entries.Sum(entry => entry.SizeBytes);

        // Retention first: delete everything outside the keep set, oldest first.
        foreach (var entry in newestFirst.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keep.Contains(entry.Id)) continue;
            if (TryDeleteDirectory(entry.Directory))
            {
                _logger.Log(LogLevel.Info, $"Retention removed snapshot {entry.Id}");
                deleted.Add(entry.Id);
                totalBytes -= entry.SizeBytes;
                removed++;
            }
        }

        // Then the hard quota: evict the oldest non-frozen snapshots until under MaxSnapshotBytes.
        if (totalBytes > config.MaxSnapshotBytes)
        {
            foreach (var entry in newestFirst.AsEnumerable().Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (totalBytes <= config.MaxSnapshotBytes) break;
                if (entry.Frozen || protectedIds.Contains(entry.Id) || deleted.Contains(entry.Id)) continue;
                if (TryDeleteDirectory(entry.Directory))
                {
                    _logger.Log(LogLevel.Info, $"Retention quota removed snapshot {entry.Id}");
                    deleted.Add(entry.Id);
                    totalBytes -= entry.SizeBytes;
                    removed++;
                }
            }
        }

        return Task.FromResult(removed);
    }

    public SnapshotStorageInfo InspectStorage(LoomPaths paths)
    {
        var info = new SnapshotStorageInfo();
        if (paths is null || !Directory.Exists(paths.SnapshotsDir)) return info;
        foreach (var directory in Directory.EnumerateDirectories(paths.SnapshotsDir))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith("corrupt-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(directory, "snapshot.json"))) continue;
            info.Count++;
            info.TotalBytes += FileSystemUtil.DirectorySize(directory);
        }

        return info;
    }

    public bool Verify(SnapshotRef reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Id)) return false;
        var directory = !string.IsNullOrWhiteSpace(reference.Path)
            ? reference.Path
            : _paths.SnapshotDir(reference.Id);
        var manifestPath = Path.Combine(directory, "snapshot.json");
        if (!File.Exists(manifestPath)) return false;

        try
        {
            if (!string.IsNullOrWhiteSpace(reference.Sha256))
            {
                var actual = Hashing.Sha256Hex(manifestPath);
                if (!string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }

            var manifest = AtomicFile.ReadJson<SnapshotManifest>(manifestPath);
            return manifest is not null;
        }
        catch
        {
            return false;
        }
    }

    private PackageArchive CopyPackageArchive(
        SnapshotManifest manifest,
        string snapshotDir,
        LoomConfig config,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.Combine(manifest.ProfileDir, "node_modules");
        if (!Directory.Exists(sourceRoot))
        {
            _logger.Log(LogLevel.Warn, "Full snapshot requested but node_modules is missing; falling back to light.");
            return Downgrade();
        }

        var files = new List<FileInfo>();
        long total = 0;
        try
        {
            foreach (var file in EnumerateArchiveFiles(sourceRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
                total += SafeLength(file);
                if (total > config.MaxArchiveBytes)
                {
                    _logger.Log(LogLevel.Warn, $"Full snapshot downgraded: node_modules exceeds MaxArchiveBytes ({config.MaxArchiveBytes} bytes).");
                    return Downgrade();
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogException(exception, "Full snapshot enumeration failed; falling back to light");
            return Downgrade();
        }

        var available = FileSystemUtil.AvailableBytes(snapshotDir);
        if (total > 0 && available < total * 2)
        {
            _logger.Log(LogLevel.Warn, "Full snapshot downgraded: not enough free disk space (needs 2x archive size).");
            return Downgrade();
        }

        var archive = new PackageArchive { Kind = "node-modules-tree" };
        var packagesRoot = Path.Combine(snapshotDir, "packages");
        Directory.CreateDirectory(packagesRoot);
        long copied = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var relative = Path.GetRelativePath(sourceRoot, file.FullName);
                var destination = Path.Combine(packagesRoot, "node_modules", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                file.CopyTo(destination, overwrite: true);
                var size = SafeLength(new FileInfo(destination));
                archive.Files.Add(new ArchivedFile
                {
                    Path = "node_modules/" + relative.Replace('\\', '/'),
                    Sha256 = Hashing.Sha256Hex(destination),
                    Size = size,
                });
                copied += size;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // node_modules may change while the app is running; skip the individual file.
                _logger.Log(LogLevel.Debug, "Full snapshot skipped a file: " + exception.Message);
            }
        }

        if (archive.Files.Count == 0)
        {
            _logger.Log(LogLevel.Warn, "Full snapshot produced no files; falling back to light.");
            FileSystemUtil.DeleteDirectoryBestEffort(packagesRoot);
            return Downgrade();
        }

        archive.TotalBytes = copied;
        return archive;

        PackageArchive Downgrade()
        {
            lock (_lastWarnings)
            {
                _lastWarnings.Add("依赖闭包归档超出限制或不可用，已降级为 light 快照（UI 文件与元数据仍完整）。");
            }

            return new PackageArchive { Kind = "none" };
        }
    }

    private static IEnumerable<FileInfo> EnumerateArchiveFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        return new DirectoryInfo(root).EnumerateFiles("*", options);
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInstallBaseline(DshInstallation installation, string bundleName)
    {
        if (DshDefaults.IsBaselineBundle(bundleName)) return true;
        return installation.BaseBundles is { Count: > 0 }
               && installation.BaseBundles.Any(bundle => string.Equals(bundle, bundleName, StringComparison.OrdinalIgnoreCase));
    }

    private static string MakeUniqueSnapshotId(LoomPaths paths, string id)
    {
        if (!Directory.Exists(paths.SnapshotDir(id))) return id;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = id + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            if (!Directory.Exists(paths.SnapshotDir(candidate))) return candidate;
        }

        return id + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    private static string BuildSnapshotId(DshInstallation installation, DateTimeOffset takenAt)
    {
        var app = string.IsNullOrWhiteSpace(installation.AppVersion) ? "unknown" : installation.AppVersion;
        var dsh = string.IsNullOrWhiteSpace(installation.DshVersion) ? "unknown" : installation.DshVersion;
        return LoomPaths.SanitizeFileName($"{takenAt:yyyyMMdd-HHmmss}-{app}-{dsh}");
    }

    private static void RememberFile(SnapshotManifest manifest, string fileName)
    {
        manifest.Files ??= new SnapshotFiles();
        switch (fileName)
        {
            case DshDefaults.ManifestFile:
                manifest.Files.PackageJson = fileName;
                break;
            case DshDefaults.LockfileFile:
                manifest.Files.PnpmLock = fileName;
                break;
            case DshDefaults.PatchFile:
                manifest.Files.CordisPatch = fileName;
                break;
            case DshDefaults.CompatibilityFile:
                manifest.Files.Compatibility = fileName;
                break;
            case DshDefaults.CordisFile:
                manifest.Files.CordisYml = fileName;
                break;
        }
    }

    private static PluginSnapshot ClonePlugin(PluginSnapshot source) => new()
    {
        Name = source.Name,
        Version = source.Version,
        Source = source.Source,
        Dir = source.Dir,
        Engines = source.Engines is null ? null : new Dictionary<string, string>(source.Engines, StringComparer.Ordinal),
        PeerDependencies = source.PeerDependencies is null
            ? null
            : new Dictionary<string, string>(source.PeerDependencies, StringComparer.Ordinal),
        Dsh = source.Dsh is null
            ? null
            : new BundleDeclaration
            {
                Bundle = source.Dsh.Bundle,
                ClientInject = source.Dsh.ClientInject?.ToList(),
                Platforms = source.Dsh.Platforms?.ToList(),
                Patch = source.Dsh.Patch?.ToList(),
            },
        Static = new StaticPrecheck
        {
            Result = source.Static.Result,
            PatchFiles = source.Static.PatchFiles.ToList(),
            ClientInject = source.Static.ClientInject.ToList(),
            MissingRefs = source.Static.MissingRefs.ToList(),
            Notes = source.Static.Notes.ToList(),
        },
        Compatibility = source.Compatibility,
        Engine = source.Engine,
        Requirements = source.Requirements.ToList(),
        CompatibilityError = source.CompatibilityError,
    };

    private sealed class RetentionEntry
    {
        public string Id { get; init; } = "";
        public string Directory { get; init; } = "";
        public DateTimeOffset TakenAt { get; init; }
        public string DshVersion { get; init; } = "";
        public bool Frozen { get; init; }
        public long SizeBytes { get; init; }
    }
}
