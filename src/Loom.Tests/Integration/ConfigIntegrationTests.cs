using System.Text;
using System.Text.Json.Nodes;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// 二期配置接线：新字段的默认值（安全默认关闭）、<see cref="ConfigStore.Normalize"/> 归一化、
/// 旧版 config.json（没有新字段）加载后走默认值、以及保存后再读回不丢字段。
/// </summary>
public sealed class ConfigIntegrationTests
{
    [Fact]
    public void UpgradeAndRecoveryFieldsHaveSafeDefaults()
    {
        var config = new LoomConfig();

        // 自动升级默认关闭：装上 Loom 不会自动改插件。
        Assert.False(config.AutoUpgradeEnabled);
        Assert.False(config.AllowPrereleaseUpgrade);
        Assert.Equal(60, config.UpgradeQueryTimeoutSeconds);
        Assert.Equal(900, config.UpgradeInstallTimeoutSeconds);
        Assert.True(config.RequireAppExitForUpgrade);

        // 包级恢复默认关闭，且默认不联网回退。
        Assert.False(config.PackageRestoreEnabled);
        Assert.False(config.PackageRestoreAllowNetworkFallback);
        Assert.Equal(600, config.PackageRestoreInstallTimeoutSeconds);
        Assert.Equal(20, config.PackageRestoreReadyTimeoutSeconds);
        Assert.Equal(5, config.KeepRollbackBackups);
    }

    [Fact]
    public void NormalizeClampsNewFieldsIntoSupportedRange()
    {
        var config = new LoomConfig
        {
            UpgradeQueryTimeoutSeconds = 0,
            UpgradeInstallTimeoutSeconds = 99999,
            PackageRestoreInstallTimeoutSeconds = 1,
            PackageRestoreReadyTimeoutSeconds = -5,
            KeepRollbackBackups = 0,
        };

        ConfigStore.Normalize(config);

        Assert.Equal(60, config.UpgradeQueryTimeoutSeconds); // 0 → 默认 60
        Assert.Equal(3600, config.UpgradeInstallTimeoutSeconds);
        Assert.Equal(30, config.PackageRestoreInstallTimeoutSeconds);
        Assert.Equal(3, config.PackageRestoreReadyTimeoutSeconds);
        Assert.Equal(5, config.KeepRollbackBackups);
    }

    [Fact]
    public void LoadsLegacyConfigWithoutNewFieldsAndKeepsDefaults()
    {
        using var sandbox = new TempDataRoot();
        var paths = sandbox.Paths;

        // 旧版 config.json：完全没有二期字段。
        var legacy = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["autoUpgradePolicy"] = "A",
            ["keepSnapshots"] = 3,
            ["theme"] = "dark",
            ["logLevel"] = "warn",
            ["allowMajorUpgrade"] = true,
        };
        sandbox.Write(paths.ConfigFile, legacy.ToJsonString());

        var loaded = ConfigStore.Load(paths);

        // 旧字段原样保留
        Assert.Equal(3, loaded.KeepSnapshots);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("warn", loaded.LogLevel);
        Assert.True(loaded.AllowMajorUpgrade);

        // 新字段走默认值（向后兼容，不抛异常、不产生非法值）
        Assert.False(loaded.AutoUpgradeEnabled);
        Assert.False(loaded.AllowPrereleaseUpgrade);
        Assert.Equal(60, loaded.UpgradeQueryTimeoutSeconds);
        Assert.Equal(900, loaded.UpgradeInstallTimeoutSeconds);
        Assert.True(loaded.RequireAppExitForUpgrade);
        Assert.False(loaded.PackageRestoreEnabled);
        Assert.False(loaded.PackageRestoreAllowNetworkFallback);
        Assert.Equal(600, loaded.PackageRestoreInstallTimeoutSeconds);
        Assert.Equal(20, loaded.PackageRestoreReadyTimeoutSeconds);
        Assert.Equal(5, loaded.KeepRollbackBackups);
    }

    [Fact]
    public void SavesAndReloadsAllNewFields()
    {
        using var sandbox = new TempDataRoot();
        var paths = sandbox.Paths;

        var config = ConfigStore.Load(paths);
        config.AutoUpgradeEnabled = true;
        config.AllowMajorUpgrade = true;
        config.AllowPrereleaseUpgrade = true;
        config.UpgradeQueryTimeoutSeconds = 90;
        config.UpgradeInstallTimeoutSeconds = 1200;
        config.RequireAppExitForUpgrade = false;
        config.PackageRestoreEnabled = true;
        config.PackageRestoreAllowNetworkFallback = true;
        config.PackageRestoreInstallTimeoutSeconds = 720;
        config.PackageRestoreReadyTimeoutSeconds = 45;
        config.KeepRollbackBackups = 9;

        ConfigStore.Save(paths, config);
        var reloaded = ConfigStore.Load(paths);

        Assert.True(reloaded.AutoUpgradeEnabled);
        Assert.True(reloaded.AllowMajorUpgrade);
        Assert.True(reloaded.AllowPrereleaseUpgrade);
        Assert.Equal(90, reloaded.UpgradeQueryTimeoutSeconds);
        Assert.Equal(1200, reloaded.UpgradeInstallTimeoutSeconds);
        Assert.False(reloaded.RequireAppExitForUpgrade);
        Assert.True(reloaded.PackageRestoreEnabled);
        Assert.True(reloaded.PackageRestoreAllowNetworkFallback);
        Assert.Equal(720, reloaded.PackageRestoreInstallTimeoutSeconds);
        Assert.Equal(45, reloaded.PackageRestoreReadyTimeoutSeconds);
        Assert.Equal(9, reloaded.KeepRollbackBackups);

        // 落盘是 camelCase（与旧字段一致），且不写 BOM。
        var bytes = File.ReadAllBytes(paths.ConfigFile);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"autoUpgradeEnabled\": true", json);
        Assert.Contains("\"packageRestoreEnabled\": true", json);
        Assert.Contains("\"keepRollbackBackups\": 9", json);
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "loom-tests", Guid.NewGuid().ToString("N"));
            Paths = new LoomPaths(Root);
            Paths.EnsureDirectories();
        }

        public string Root { get; }
        public LoomPaths Paths { get; }

        public void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
