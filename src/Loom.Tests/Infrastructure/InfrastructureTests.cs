using System.Text;
using Loom.Core.Cli;
using Loom.Core.Infrastructure;
using Loom.Core.Ipc;
using Xunit;

namespace Loom.Tests.Infrastructure;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "loom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public LoomPaths Paths => LoomPaths.Create(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

public sealed class SemVersionTests
{
    [Theory]
    [InlineData("0.2.2-rc.1", 0, 2, 2, "rc.1")]
    [InlineData("1.73.19", 1, 73, 19, null)]
    [InlineData("v10.0.8", 10, 0, 8, null)]
    public void Parses(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SemVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Fact]
    public void OrdersPrereleaseBeforeRelease()
    {
        Assert.True(SemVersion.Parse("0.2.2-rc.1") < SemVersion.Parse("0.2.2"));
        Assert.True(SemVersion.Parse("0.2.2-rc.1") < SemVersion.Parse("0.2.2-rc.2"));
        Assert.True(SemVersion.Parse("0.2.2") < SemVersion.Parse("0.3.0"));
    }

    [Theory]
    [InlineData("deepseek-harness-0.3.0-win-x64.exe", "0.3.0")]
    [InlineData("deepseek-harness-0.2.2-rc.1-win-x64.exe", "0.2.2-rc.1")]
    public void ParsesInstallerFileNames(string fileName, string expected)
    {
        Assert.True(SemVersion.TryParseFromFileName(fileName, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    public void RejectsInvalid(string text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }
}

public sealed class AtomicFileTests
{
    [Fact]
    public void WritesUtf8WithoutBomAndReplaces()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "nested", "config.json");
        AtomicFile.WriteAllText(path, "{\"a\":\"中文\"}");
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("{\"a\":\"中文\"}", File.ReadAllText(path, Encoding.UTF8));

        AtomicFile.WriteAllText(path, "{\"a\":2}");
        Assert.Equal("{\"a\":2}", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void CorruptJsonIsQuarantined()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "state.json");
        File.WriteAllText(path, "{ broken");
        var state = AtomicFile.ReadJsonSafe<Loom.Core.Models.LoomState>(path);
        Assert.Null(state);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(temp.Path, "state.json.corrupt-*"));
    }

    [Fact]
    public void FailedAtomicWriteLeavesExistingFileIntact()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "package.json");
        File.WriteAllText(path, "{\"original\":true}");

        // 目标文件被独占打开时 rename 会失败；原子写必须保留原内容并清掉临时文件。
        // （Windows 可能把共享冲突映射为 IOException 或 UnauthorizedAccessException，这里只要求失败。）
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "{\"replacement\":true}"));
        }

        Assert.Equal("{\"original\":true}", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "package.json.tmp-*"));
    }
}

public sealed class ConfigStoreTests
{
    [Fact]
    public void RoundTripsAndNormalizes()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        var config = new Loom.Core.Models.LoomConfig
        {
            KeepSnapshots = 0,
            Theme = "weird",
            LogLevel = "",
            MaxSnapshotBytes = 1,
            OperationTimeoutSeconds = 1,
        };
        ConfigStore.Save(paths, config);
        var loaded = ConfigStore.Load(paths);
        Assert.True(loaded.KeepSnapshots >= 1);
        Assert.Equal("system", loaded.Theme);
        Assert.Equal("info", loaded.LogLevel);
        Assert.True(loaded.MaxSnapshotBytes >= 50L * 1024 * 1024);
        Assert.True(loaded.OperationTimeoutSeconds >= 5);
    }
}

public sealed class StateStoreTests
{
    [Fact]
    public void RoundTripsSnapshotPointers()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        var state = new Loom.Core.Models.LoomState
        {
            Phase = Loom.Core.Models.UpdatePhase.SnapshotReady,
            LastSnapshot = new Loom.Core.Models.SnapshotRef
            {
                Id = "20261114-120000-0.2.2-rc.1",
                Path = @"C:\data\snapshots\x",
                AppVersion = "0.2.2-rc.1",
                DshVersion = "0.2.2-rc.1",
                PluginCount = 3,
            },
            PendingUpdate = new Loom.Core.Models.PendingUpdate { TargetVersion = "0.3.0", FileName = "x.exe" },
        };
        StateStore.Save(paths, state);
        var loaded = StateStore.Load(paths);
        Assert.Equal(Loom.Core.Models.UpdatePhase.SnapshotReady, loaded.Phase);
        Assert.Equal("20261114-120000-0.2.2-rc.1", loaded.LastSnapshot?.Id);
        Assert.Equal("0.3.0", loaded.PendingUpdate?.TargetVersion);
    }
}

public sealed class RedactorTests
{
    [Theory]
    [InlineData("--_authToken=abcdef123", "abcdef123")]
    [InlineData("Authorization: Bearer supersecrettoken", "supersecrettoken")]
    [InlineData("https://registry.example.com/pkg?token=secretvalue", "secretvalue")]
    public void RemovesSecrets(string input, string secret)
    {
        var redacted = Redactor.Redact(input);
        Assert.DoesNotContain(secret, redacted);
    }
}

public sealed class CliParserTests
{
    [Fact]
    public void EmptyArgsDefaultsToStatus()
    {
        var invocation = CliParser.Parse(Array.Empty<string>());
        Assert.Equal("status", invocation.Command);
        Assert.False(invocation.CommandWasExplicit);
    }

    [Fact]
    public void ParsesCommandAndOptions()
    {
        var invocation = CliParser.Parse(new[]
        {
            "restore", "--dry-run", "--force", "--snapshot", "abc", "--data-root", @"D:\tmp", "--json",
        });
        Assert.Equal("restore", invocation.Command);
        Assert.True(invocation.DryRun);
        Assert.True(invocation.Force);
        Assert.True(invocation.Json);
        Assert.Equal("abc", invocation.SnapshotId);
        Assert.Equal(@"D:\tmp", invocation.DataRoot);
    }

    [Fact]
    public void ParsesUiTarget()
    {
        var invocation = CliParser.Parse(new[] { "ui", "settings" });
        Assert.True(invocation.IsUi);
        Assert.Equal("settings", invocation.UiTarget);
    }

    [Fact]
    public void TrayFlag()
    {
        var invocation = CliParser.Parse(new[] { "--tray" });
        Assert.True(invocation.Tray);
    }

    [Fact]
    public void UnknownOptionIsError()
    {
        var invocation = CliParser.Parse(new[] { "status", "--nope" });
        Assert.NotNull(invocation.Error);
    }
}

public sealed class QueueIpcTests
{
    [Fact]
    public async Task QueueExecutesAndReturnsResult()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        paths.EnsureDirectories();

        using var server = new QueueServer(paths, (args, _) => Task.FromResult(new CliProcessResult
        {
            ExitCode = 0,
            StdOut = "echo:" + string.Join(" ", args),
        }));
        server.Start();

        var client = new QueueClient(paths);
        var response = await client.SubmitAsync(new[] { "status", "--json" }, TimeSpan.FromSeconds(10));

        Assert.NotNull(response);
        Assert.Equal(0, response!.ExitCode);
        Assert.Contains("echo:status --json", response.StdOut);
    }

    [Fact]
    public async Task QueueTimesOutWhenNoServer()
    {
        using var temp = new TempDir();
        var paths = temp.Paths;
        paths.EnsureDirectories();
        var client = new QueueClient(paths);
        var response = await client.SubmitAsync(new[] { "status" }, TimeSpan.FromMilliseconds(400));
        Assert.Null(response);
        Assert.Empty(Directory.GetFiles(paths.QueueRequestsDir));
    }

    [Fact]
    public void QueueWorkerLockIsExclusivePerDataRoot()
    {
        using var first = new TempDir();
        using var second = new TempDir();
        var paths = first.Paths;
        using var serverA = new QueueServer(paths, (_, _) => Task.FromResult(new CliProcessResult()));
        using var serverB = new QueueServer(paths, (_, _) => Task.FromResult(new CliProcessResult()));
        using var serverOtherRoot = new QueueServer(second.Paths, (_, _) => Task.FromResult(new CliProcessResult()));

        Assert.NotEqual(QueueServer.WorkerLockPathFor(paths), QueueServer.WorkerLockPathFor(second.Paths));

        using var held = serverA.TryAcquireWorkerLock();
        Assert.NotNull(held);
        Assert.Null(serverB.TryAcquireWorkerLock()); // 同一数据根：第二个泵必须让位

        using var other = serverOtherRoot.TryAcquireWorkerLock();
        Assert.NotNull(other); // 不同数据根：互不阻塞（P3-R1）
    }
}
