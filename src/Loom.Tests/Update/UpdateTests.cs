using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Update;
using Xunit;

namespace Loom.Tests.Update;

/// <summary>Pure feed helpers: tag parsing / version comparison / fixed asset URL.</summary>
public sealed class UpdateFeedTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", "0.2.0")]
    [InlineData("0.2.0", "0.1.0", "0.2.0")]
    [InlineData("V0.10.0", "0.9.9", "0.10.0")]
    [InlineData("v1.0.0-rc.1", "0.1.0", "1.0.0-rc.1")]
    public void ParseNewerVersion_ReturnsNewerVersion(string remote, string current, string expected)
    {
        Assert.Equal(expected, GitHubReleaseFeed.ParseNewerVersion(remote, current));
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("v0.0.9", "0.1.0")]
    [InlineData("v0.1.0-beta", "0.1.0")]
    [InlineData("not-a-version", "0.1.0")]
    [InlineData("", "0.1.0")]
    [InlineData(null, "0.1.0")]
    public void ParseNewerVersion_ReturnsNullWhenNotNewerOrUnparsable(string? remote, string current)
    {
        Assert.Null(GitHubReleaseFeed.ParseNewerVersion(remote, current));
    }

    [Theory]
    [InlineData("/Three-Freezen/loom-dsh/releases/tag/v0.2.0", "v0.2.0")]
    [InlineData("/Three-Freezen/loom-dsh/releases/tag/v1.0.0-rc.1", "v1.0.0-rc.1")]
    [InlineData("https://github.com/Three-Freezen/loom-dsh/releases/tag/v0.3.0?foo=1", "v0.3.0")]
    [InlineData("/Three-Freezen/loom-dsh/releases", null)]
    [InlineData("/Three-Freezen/loom-dsh/releases/latest", null)]
    [InlineData(null, null)]
    public void ExtractTag_ReadsRedirectLocation(string? path, string? expected)
    {
        Assert.Equal(expected, GitHubReleaseFeed.ExtractTag(path));
    }

    [Fact]
    public void AssetUrl_UsesFixedNameOnLatestAlias()
    {
        Assert.Equal(
            "https://github.com/Three-Freezen/loom-dsh/releases/latest/download/loom-dsh-Setup.exe",
            GitHubReleaseFeed.AssetUrl);
        Assert.Equal("loom-dsh-Setup.exe", GitHubReleaseFeed.AssetName);
        Assert.Equal(AppInstallInfo.SetupAssetName, GitHubReleaseFeed.AssetName);
    }
}

/// <summary>State machine / local-source end-to-end (no network): check → download → cleanup.</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const long MinimumAssetBytes = 1024 * 1024;

    private readonly string _root;
    private readonly LoomPaths _paths;
    private readonly LoomConfig _config = new()
    {
        AutoCheckUpdate = true,
        DeleteSetupAfterUpdate = true,
    };

    public UpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "loom-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new LoomPaths(Path.Combine(_root, "data"));
        _paths.EnsureDirectories();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort: temp directory
        }
    }

    private UpdateService CreateService(bool installed = true)
    {
        var service = new UpdateService(_paths, () => _config, _ => { });
        var installDir = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDir);
        if (installed) File.WriteAllText(Path.Combine(installDir, "unins000.exe"), "");
        service.InstallInfo = AppInstallInfo.ForDirectory(installDir);
        return service;
    }

    private string CreateSourceAsset(long bytes = MinimumAssetBytes * 2)
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var asset = Path.Combine(source, AppInstallInfo.SetupAssetName);
        File.WriteAllBytes(asset, new byte[bytes]);
        return source;
    }

    private string DownloadPath => Path.Combine(_root, "downloads", AppInstallInfo.SetupAssetName);

    [Fact]
    public async Task CheckAsync_WithNewerTag_ReportsAvailable()
    {
        var service = CreateService();
        service.LatestTagOverride = "v0.2.0";

        var result = await service.CheckAsync();

        Assert.NotNull(result);
        Assert.Equal("0.2.0", result!.Version);
        Assert.Equal(UpdateState.Available, service.State);
        Assert.Equal("0.2.0", service.NewVersion);
        Assert.Null(service.ErrorText);
        Assert.Contains("/releases/latest", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_WithOlderOrEqualTag_ReportsUpToDate()
    {
        var service = CreateService();
        service.LatestTagOverride = "v0.1.0-beta";

        var result = await service.CheckAsync();

        Assert.Null(result);
        Assert.Equal(UpdateState.UpToDate, service.State);
        Assert.Null(service.NewVersion);
        Assert.False(string.IsNullOrWhiteSpace(service.MessageText));
    }

    [Fact]
    public async Task CheckAsync_PersistsLastCheckTime()
    {
        LoomConfig? saved = null;
        var service = new UpdateService(_paths, () => _config, config => saved = config)
        {
            LatestTagOverride = "v0.2.0",
        };

        await service.CheckAsync();

        Assert.NotNull(saved);
        Assert.True(DateTimeOffset.UtcNow - saved!.LastUpdateCheckUtc < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CheckAsync_RaisesStateChanged()
    {
        var service = CreateService();
        service.LatestTagOverride = "v0.2.0";
        var changes = 0;
        service.StateChanged += () => changes++;

        await service.CheckAsync();

        Assert.True(changes >= 2, $"expected Checking + final state events, got {changes}");
    }

    [Fact]
    public async Task AutoCheckIfDue_SkipsWhenCheckedRecently()
    {
        _config.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        var service = CreateService();

        var result = await service.AutoCheckIfDueAsync();

        Assert.Null(result);
        Assert.Equal(UpdateState.Idle, service.State);
    }

    [Fact]
    public async Task AutoCheckIfDue_SkipsWhenDisabled()
    {
        _config.AutoCheckUpdate = false;
        var service = CreateService();

        var result = await service.AutoCheckIfDueAsync();

        Assert.Null(result);
        Assert.Equal(UpdateState.Idle, service.State);
    }

    [Fact]
    public async Task AutoCheckIfDue_SkipsPortableBuilds()
    {
        var service = CreateService(installed: false);

        var result = await service.AutoCheckIfDueAsync();

        Assert.Null(result);
        Assert.False(service.InAppUpdateSupported);
    }

    [Fact]
    public async Task DownloadAsync_FromLocalSource_CopiesAssetAndMarksReady()
    {
        var service = CreateService();
        service.LatestTagOverride = "v0.2.0";
        service.SourceOverride = CreateSourceAsset();
        service.DownloadPathOverride = DownloadPath;

        await service.CheckAsync();
        var downloaded = await service.DownloadAsync();

        Assert.True(downloaded);
        Assert.Equal(UpdateState.Ready, service.State);
        Assert.True(File.Exists(DownloadPath));
        Assert.True(new FileInfo(DownloadPath).Length >= MinimumAssetBytes);
        Assert.True(File.Exists(service.PendingSetupCleanupPath));
        Assert.Equal(100, service.ProgressPercent);
    }

    [Fact]
    public async Task DownloadAsync_WithTooSmallAsset_FailsWithoutLeavingTarget()
    {
        var service = CreateService();
        service.LatestTagOverride = "v0.2.0";
        service.SourceOverride = CreateSourceAsset(bytes: 1024);
        service.DownloadPathOverride = DownloadPath;

        await service.CheckAsync();
        var downloaded = await service.DownloadAsync();

        Assert.False(downloaded);
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.False(File.Exists(DownloadPath));
        Assert.False(string.IsNullOrWhiteSpace(service.ErrorText));
    }

    [Fact]
    public void DownloadAsync_WithoutCheck_Fails()
    {
        var service = CreateService();
        service.DownloadPathOverride = DownloadPath;

        var downloaded = service.DownloadAsync().GetAwaiter().GetResult();

        Assert.False(downloaded);
        Assert.Equal(UpdateState.Idle, service.State); // unchanged: nothing to download
        Assert.False(string.IsNullOrWhiteSpace(service.ErrorText));
        Assert.False(File.Exists(DownloadPath));
    }

    [Fact]
    public void ApplyAndRestart_OnPortableBuild_Throws()
    {
        var service = CreateService(installed: false);
        service.DownloadPathOverride = DownloadPath;

        var exception = Assert.Throws<InvalidOperationException>(() => service.ApplyAndRestart());
        Assert.Contains("免安装", exception.Message);
    }

    [Fact]
    public void ApplyAndRestart_WithoutDownloadedSetup_Throws()
    {
        var service = CreateService(installed: true);
        service.DownloadPathOverride = DownloadPath;

        var exception = Assert.Throws<InvalidOperationException>(() => service.ApplyAndRestart());
        Assert.Contains("尚未下载", exception.Message);
    }

    [Fact]
    public void ConsumePendingSetupCleanup_DeletesInstallerAfterUpgrade()
    {
        var setup = Path.Combine(_root, "downloads", AppInstallInfo.SetupAssetName);
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllBytes(setup, new byte[MinimumAssetBytes + 1]);
        var marker = Path.Combine(_paths.DataRoot, "pending-setup-cleanup.txt");
        File.WriteAllText(marker, setup + Environment.NewLine + "0.0.9" + Environment.NewLine);

        var service = CreateService();
        service.ConsumePendingSetupCleanup();

        Assert.False(File.Exists(setup));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ConsumePendingSetupCleanup_SameVersionKeepsInstallerButClearsMarker()
    {
        var setup = Path.Combine(_root, "downloads", AppInstallInfo.SetupAssetName);
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllBytes(setup, new byte[MinimumAssetBytes + 1]);
        var marker = Path.Combine(_paths.DataRoot, "pending-setup-cleanup.txt");
        File.WriteAllText(marker, setup + Environment.NewLine + LoomPaths.ToolVersion + Environment.NewLine);

        var service = CreateService();
        service.ConsumePendingSetupCleanup();

        Assert.True(File.Exists(setup));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ConsumePendingSetupCleanup_IgnoresForeignSetupName()
    {
        var setup = Path.Combine(_root, "downloads", "SomethingElse.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllBytes(setup, new byte[16]);
        var marker = Path.Combine(_paths.DataRoot, "pending-setup-cleanup.txt");
        File.WriteAllText(marker, setup + Environment.NewLine + "0.0.9" + Environment.NewLine);

        var service = CreateService();
        service.ConsumePendingSetupCleanup();

        Assert.True(File.Exists(setup));
        Assert.False(File.Exists(marker));
    }
}
