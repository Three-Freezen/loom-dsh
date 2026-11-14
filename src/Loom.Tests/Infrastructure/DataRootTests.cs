using Loom.Core.Cli;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>
/// Data-root resolution (install-dir Data / portable flag / legacy %LOCALAPPDATA%), legacy-data
/// migration and the future in-app-update contract. No test touches the real %LOCALAPPDATA%\Loom:
/// the legacy root is redirected through the test-only LOOM_LEGACY_DATA_ROOT environment variable.
/// </summary>
public sealed class DataRootResolverTests
{
    private static void SetLegacyRoot(string? value) =>
        System.Environment.SetEnvironmentVariable(DataRootResolver.LegacyRootEnvironmentVariable, value);

    private static string CreateInstalledDirectory(TempDir temp, bool withPortableFlag = false)
    {
        var baseDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, DataRootResolver.UninstallerFileName), "stub");
        if (withPortableFlag)
        {
            var dataDir = Path.Combine(baseDir, DataRootResolver.DataDirectoryName);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, DataRootResolver.PortableFlagFileName), "portable");
        }

        return baseDir;
    }

    [Fact]
    public void ExplicitRootWinsOverPortableAndInstalledBuild()
    {
        using var temp = new TempDir();
        var baseDir = CreateInstalledDirectory(temp, withPortableFlag: true);
        var explicitRoot = Path.Combine(temp.Path, "custom-data");

        var resolution = DataRootResolver.Resolve(explicitRoot, baseDir);

        Assert.Equal(DataRootSource.Explicit, resolution.Source);
        Assert.Equal(Path.GetFullPath(explicitRoot), resolution.Root);
        Assert.True(resolution.IsInstalledBuild);
        Assert.False(resolution.IsPortable);
    }

    [Fact]
    public void PortableFlagWinsOverInstalledBuild()
    {
        using var temp = new TempDir();
        var baseDir = CreateInstalledDirectory(temp, withPortableFlag: true);

        var resolution = DataRootResolver.Resolve(null, baseDir);

        Assert.Equal(DataRootSource.Portable, resolution.Source);
        Assert.True(resolution.UsesInstalledDataDirectory);
        Assert.Equal(Path.Combine(baseDir, DataRootResolver.DataDirectoryName), resolution.Root);
        Assert.True(resolution.IsInstalledBuild);
    }

    [Fact]
    public void InstalledBuildUsesDataDirectoryNextToTheExecutable()
    {
        using var temp = new TempDir();
        var baseDir = CreateInstalledDirectory(temp);

        var resolution = DataRootResolver.Resolve(null, baseDir);

        Assert.Equal(DataRootSource.Installed, resolution.Source);
        Assert.Equal(Path.Combine(baseDir, DataRootResolver.DataDirectoryName), resolution.Root);
        Assert.True(resolution.UsesInstalledDataDirectory);
        Assert.True(resolution.IsInstalledBuild);
        Assert.False(resolution.IsPortable);
    }

    [Fact]
    public void DevelopmentBuildFallsBackToLegacyDefault()
    {
        using var temp = new TempDir();
        var baseDir = Path.Combine(temp.Path, "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(baseDir);
        var fakeLegacy = Path.Combine(temp.Path, "fake-localappdata", "Loom");
        SetLegacyRoot(fakeLegacy);
        try
        {
            var resolution = DataRootResolver.Resolve(null, baseDir);

            Assert.Equal(DataRootSource.LegacyDefault, resolution.Source);
            Assert.Equal(Path.GetFullPath(fakeLegacy), resolution.Root);
            Assert.False(resolution.IsInstalledBuild);
            Assert.False(resolution.UsesInstalledDataDirectory);
        }
        finally
        {
            SetLegacyRoot(null);
        }
    }

    [Fact]
    public void LegacyDefaultIsLatestLocalApplicationDataLoomWithoutOverride()
    {
        using var temp = new TempDir();
        var baseDir = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(baseDir);
        SetLegacyRoot(null);
        try
        {
            var resolution = DataRootResolver.Resolve(null, baseDir);
            var expected = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Loom");

            Assert.Equal(DataRootSource.LegacyDefault, resolution.Source);
            Assert.Equal(Path.GetFullPath(expected), resolution.Root);
        }
        finally
        {
            SetLegacyRoot(null);
        }
    }
}

public sealed class DataMigrationTests
{
    private static string WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string CreateLegacyRoot(TempDir temp)
    {
        var source = Path.Combine(temp.Path, "legacy");
        WriteFile(source, "config.json", "{\"schemaVersion\":1}");
        WriteFile(source, "state.json", "{\"schemaVersion\":1}");
        WriteFile(source, "snapshots/s1/snapshot.json", "{}");
        WriteFile(source, "reports/check-1.md", "# report");
        WriteFile(source, "logs/loom.log", "log line");
        WriteFile(source, "helper/helper.mjs", "// helper");
        WriteFile(source, "queue/requests/req.json", "{}");
        WriteFile(source, "queue/results/result.json", "{}");
        WriteFile(source, "loom.operation.lock", "");
        WriteFile(source, "notes.tmp-abc123", "");
        WriteFile(source, "state.json.corrupt-20260101-000000", "{}");
        return source;
    }

    [Fact]
    public void CopiesFilesSkipsTransientFilesAndWritesMarker()
    {
        using var temp = new TempDir();
        var source = CreateLegacyRoot(temp);
        var target = Path.Combine(temp.Path, "install", "Data");

        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = source,
            TargetRoot = target,
        });

        Assert.True(result.Success, result.Message + " | " + string.Join(";", result.Errors));
        Assert.Null(result.SkippedReason);
        Assert.Equal(6, result.FilesCopied);
        Assert.Equal(5, result.FilesSkipped);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(target, "config.json")));
        Assert.True(File.Exists(Path.Combine(target, "state.json")));
        Assert.True(File.Exists(Path.Combine(target, "snapshots", "s1", "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(target, "reports", "check-1.md")));
        Assert.True(File.Exists(Path.Combine(target, "helper", "helper.mjs")));
        Assert.False(Directory.Exists(Path.Combine(target, "queue")));
        Assert.False(File.Exists(Path.Combine(target, "loom.operation.lock")));
        Assert.False(File.Exists(Path.Combine(target, "notes.tmp-abc123")));
        Assert.False(File.Exists(Path.Combine(target, "state.json.corrupt-20260101-000000")));

        var markerPath = DataMigration.MarkerPathFor(target);
        Assert.True(result.MarkerWritten);
        Assert.True(File.Exists(markerPath));
        var record = AtomicFile.ReadJson<DataMigrationRecord>(markerPath);
        Assert.NotNull(record);
        Assert.Equal(Path.GetFullPath(source), record!.SourceRoot);
        Assert.Equal(Path.GetFullPath(target), record.TargetRoot);
        Assert.True(record.FilesCopied > 0);
        Assert.Equal(0, record.Errors);
    }

    [Fact]
    public void NeverOverwritesExistingTargetFiles()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "legacy");
        WriteFile(source, "config.json", "{\"schemaVersion\":1}");
        WriteFile(source, "notes.txt", "old");
        var target = Path.Combine(temp.Path, "install", "Data");
        WriteFile(target, "notes.txt", "new");

        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = source,
            TargetRoot = target,
        });

        Assert.True(result.Success);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "notes.txt")));
        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public void IsIdempotentOnceTargetIsInitialized()
    {
        using var temp = new TempDir();
        var source = CreateLegacyRoot(temp);
        var target = Path.Combine(temp.Path, "install", "Data");
        var options = new DataMigrationOptions { SourceRoot = source, TargetRoot = target };

        var first = DataMigration.Migrate(options);
        Assert.True(first.Success);

        WriteFile(source, "late-arrival.txt", "added after migration");
        var second = DataMigration.Migrate(options);

        Assert.True(second.Success);
        Assert.Equal("target-initialized", second.SkippedReason);
        Assert.Equal(0, second.FilesCopied);
        Assert.False(File.Exists(Path.Combine(target, "late-arrival.txt")));
    }

    [Fact]
    public void RewritesLegacyAbsolutePathsInConfigAndState()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "legacy");
        var target = Path.Combine(temp.Path, "install", "Data");
        var sourcePaths = LoomPaths.Create(source);
        var config = new LoomConfig
        {
            DataRoot = source,
            InstallDir = @"D:\DeepSeek",
        };
        ConfigStore.Save(sourcePaths, config);
        var state = new LoomState
        {
            LastSnapshot = new SnapshotRef { Id = "s1", Path = Path.Combine(source, "snapshots", "s1") },
            PreUpdateSnapshot = new SnapshotRef { Id = "s0", Path = Path.Combine(source, "snapshots", "s0") },
            LastOperation = new LastOperation
            {
                Id = "op1",
                Kind = "snapshot",
                ReportPath = Path.Combine(source, "reports", "op1.md"),
            },
            LastCheck = new LastCheckSummary
            {
                ReportPath = Path.Combine(source, "reports", "check1.json"),
            },
        };
        StateStore.Save(sourcePaths, state);

        var result = DataMigration.Migrate(new DataMigrationOptions { SourceRoot = source, TargetRoot = target });

        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Equal(5, result.PathsRewritten);
        var targetPaths = LoomPaths.Create(target);
        var migratedState = StateStore.Load(targetPaths);
        Assert.Equal(Path.Combine(target, "snapshots", "s1"), migratedState.LastSnapshot!.Path);
        Assert.Equal(Path.Combine(target, "snapshots", "s0"), migratedState.PreUpdateSnapshot!.Path);
        Assert.Equal(Path.Combine(target, "reports", "op1.md"), migratedState.LastOperation!.ReportPath);
        Assert.Equal(Path.Combine(target, "reports", "check1.json"), migratedState.LastCheck!.ReportPath);

        var migratedConfig = ConfigStore.Load(targetPaths);
        Assert.Equal(Path.GetFullPath(target), migratedConfig.DataRoot);
        Assert.Equal(@"D:\DeepSeek", migratedConfig.InstallDir);
    }

    [Fact]
    public void RemoveSourceDeletesLegacyRootAfterSuccess()
    {
        using var temp = new TempDir();
        var source = CreateLegacyRoot(temp);
        var target = Path.Combine(temp.Path, "install", "Data");

        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = source,
            TargetRoot = target,
            RemoveSource = true,
        });

        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.True(result.SourceRemoved);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "config.json")));
        var record = AtomicFile.ReadJson<DataMigrationRecord>(DataMigration.MarkerPathFor(target));
        Assert.True(record!.RemoveSource);
    }

    [Fact]
    public void DryRunWritesNothing()
    {
        using var temp = new TempDir();
        var source = CreateLegacyRoot(temp);
        var target = Path.Combine(temp.Path, "install", "Data");

        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = source,
            TargetRoot = target,
            DryRun = true,
        });

        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.Equal(6, result.FilesCopied);
        Assert.False(Directory.Exists(target));
        Assert.False(result.MarkerWritten);
        Assert.NotNull(result.MarkerPath);
        Assert.False(File.Exists(result.MarkerPath!));
    }

    [Fact]
    public void MissingSourceIsSkippedGracefully()
    {
        using var temp = new TempDir();
        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = Path.Combine(temp.Path, "does-not-exist"),
            TargetRoot = Path.Combine(temp.Path, "install", "Data"),
        });

        Assert.True(result.Success);
        Assert.Equal("source-missing", result.SkippedReason);
    }

    [Fact]
    public void SameRootIsRejected()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(root);

        var result = DataMigration.Migrate(new DataMigrationOptions
        {
            SourceRoot = root,
            TargetRoot = root,
        });

        Assert.False(result.Success);
        Assert.Equal("same-root", result.ErrorCode);
    }

    [Fact]
    public void RewritePathOnlyTouchesPathsUnderTheOldRoot()
    {
        Assert.Equal(@"C:\new\snapshots\s1", DataMigration.RewritePath(
            @"C:\old\snapshots\s1", @"C:\old", @"C:\new"));
        Assert.Equal(@"C:\new", DataMigration.RewritePath(@"C:\old", @"C:\old", @"C:\new"));
        Assert.Equal(@"D:\DeepSeek", DataMigration.RewritePath(@"D:\DeepSeek", @"C:\old", @"C:\new"));
        Assert.Equal(@"C:\older\file", DataMigration.RewritePath(@"C:\older\file", @"C:\old", @"C:\new"));
        // Case-insensitive on Windows.
        Assert.Equal(@"C:\new\a.txt", DataMigration.RewritePath(@"c:\OLD\a.txt", @"C:\old", @"C:\new"));
    }

    [Fact]
    public void AutoMigrationRunsOnlyForInstalledEmptyDataRoot()
    {
        using var temp = new TempDir();
        var baseDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, DataRootResolver.UninstallerFileName), "stub");
        var legacy = CreateLegacyRoot(temp);
        System.Environment.SetEnvironmentVariable(DataRootResolver.LegacyRootEnvironmentVariable, legacy);
        try
        {
            var resolution = DataRootResolver.Resolve(null, baseDir);
            Assert.Equal(DataRootSource.Installed, resolution.Source);
            var paths = LoomPaths.Create(resolution.Root);

            var first = DataMigration.TryAutoMigrate(paths, resolution);
            Assert.NotNull(first);
            Assert.True(first!.Success, string.Join(";", first.Errors));
            Assert.True(File.Exists(paths.ConfigFile));
            Assert.True(File.Exists(DataMigration.MarkerPathFor(resolution.Root)));

            // Second start: config.json now exists → no second migration.
            Assert.Null(DataMigration.TryAutoMigrate(paths, resolution));

            // Explicit --data-root never auto-migrates.
            var explicitResolution = DataRootResolver.Resolve(Path.Combine(temp.Path, "custom"), baseDir);
            var explicitPaths = LoomPaths.Create(explicitResolution.Root);
            Assert.Null(DataMigration.TryAutoMigrate(explicitPaths, explicitResolution));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(DataRootResolver.LegacyRootEnvironmentVariable, null);
        }
    }

    [Fact]
    public void AutoMigrationWithoutLegacyDataDoesNothing()
    {
        using var temp = new TempDir();
        var baseDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, DataRootResolver.UninstallerFileName), "stub");
        System.Environment.SetEnvironmentVariable(
            DataRootResolver.LegacyRootEnvironmentVariable,
            Path.Combine(temp.Path, "no-legacy-here"));
        try
        {
            var resolution = DataRootResolver.Resolve(null, baseDir);
            var paths = LoomPaths.Create(resolution.Root);
            Assert.Null(DataMigration.TryAutoMigrate(paths, resolution));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(DataRootResolver.LegacyRootEnvironmentVariable, null);
        }
    }
}

public sealed class AppInstallInfoTests
{
    [Fact]
    public void DetectsInstalledBuildFromUninstaller()
    {
        using var temp = new TempDir();
        var baseDir = Path.Combine(temp.Path, "Loom");
        Directory.CreateDirectory(baseDir);

        var development = AppInstallInfo.ForDirectory(baseDir);
        Assert.False(development.IsInstalledBuild);
        Assert.Null(development.InstallDir);
        Assert.Null(development.DataRoot);

        File.WriteAllText(Path.Combine(baseDir, AppInstallInfo.UninstallerFileName), "stub");
        var installed = AppInstallInfo.ForDirectory(baseDir);
        Assert.True(installed.IsInstalledBuild);
        Assert.Equal(Path.GetFullPath(baseDir), installed.InstallDir);
        Assert.Equal(Path.Combine(Path.GetFullPath(baseDir), "Data"), installed.DataRoot);
    }

    [Fact]
    public void UsesFixedInstallerAssetName()
    {
        Assert.Equal("loom-dsh-Setup.exe", AppInstallInfo.SetupAssetName);
        Assert.Equal("loom-dsh.exe", AppInstallInfo.MainExecutableName);
        Assert.EndsWith(Path.Combine("Programs", "Loom"), AppInstallInfo.DefaultInstallDir);
    }

    [Fact]
    public void BuildsSilentUpgradeArgumentsForCurrentInstallDirectory()
    {
        var arguments = AppInstallInfo.BuildSilentUpgradeArguments(@"D:\Apps\Loom\");
        Assert.Equal(@"/SILENT /DIR=""D:\Apps\Loom""", arguments);
    }

    [Fact]
    public void RejectsEmptyInstallDirectory()
    {
        Assert.Throws<ArgumentException>(() => AppInstallInfo.BuildSilentUpgradeArguments(""));
    }
}

public sealed class MigrateDataCliTests
{
    [Fact]
    public void ParsesMigrateDataCommandAndFlags()
    {
        var invocation = CliParser.Parse(new[] { "migrate-data", "--dry-run", "--json", "--remove-source" });

        Assert.Equal("migrate-data", invocation.Command);
        Assert.True(invocation.DryRun);
        Assert.True(invocation.Json);
        Assert.True(invocation.RemoveSource);
        Assert.Null(invocation.Error);
    }

    [Fact]
    public void RemoveSourceIsOptIn()
    {
        var invocation = CliParser.Parse(new[] { "migrate-data" });

        Assert.Equal("migrate-data", invocation.Command);
        Assert.False(invocation.RemoveSource);
        Assert.False(invocation.DryRun);
    }
}
