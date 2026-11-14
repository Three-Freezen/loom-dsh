using System.Text;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>
/// config/state 迁移前备份（P2-4）：旧 schema/归一化会改内容时先生成 &lt;file&gt;.bak-&lt;ts&gt;；
/// 损坏/未来 schema 在 quarantine 前也要留一份备份；备份失败不影响加载。
/// </summary>
public sealed class StateAndConfigMigrationTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string Write(TempDir temp, string path, string content)
    {
        File.WriteAllText(path, content, Utf8NoBom);
        return content;
    }

    [Fact]
    public void OldSchemaConfigIsBackedUpBeforeNormalization()
    {
        using var temp = new TempDir();
        var original = Write(
            temp,
            temp.Paths.ConfigFile,
            "{\n  \"schemaVersion\": 0,\n  \"keepSnapshots\": 0,\n  \"operationTimeoutSeconds\": 0,\n  \"theme\": \"weird\"\n}");

        var config = ConfigStore.Load(temp.Paths);

        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(1, config.KeepSnapshots);
        Assert.Equal(5, config.OperationTimeoutSeconds);
        Assert.Equal("system", config.Theme);
        var backup = Assert.Single(Directory.GetFiles(temp.Path, "config.json.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup, Encoding.UTF8));
    }

    [Fact]
    public void SaveBacksUpExistingFileWhenValuesNeedNormalization()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Paths.ConfigFile, "{\"schemaVersion\":1,\"keepSnapshots\":5}", Utf8NoBom);

        ConfigStore.Save(temp.Paths, new LoomConfig { KeepSnapshots = 0 });

        Assert.Single(Directory.GetFiles(temp.Path, "config.json.bak-*"));
        var saved = ConfigStore.Load(temp.Paths);
        Assert.Equal(1, saved.KeepSnapshots);
    }

    [Fact]
    public void OldSchemaStateIsBackedUpAndPointersSurvive()
    {
        using var temp = new TempDir();
        var original = Write(
            temp,
            temp.Paths.StateFile,
            "{\n  \"schemaVersion\": 0,\n  \"lastSnapshot\": { \"id\": \"snap-1\", \"path\": \"C:\\\\data\\\\snap-1\" }\n}");

        var state = StateStore.Load(temp.Paths);

        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal("snap-1", state.LastSnapshot?.Id);
        var backup = Assert.Single(Directory.GetFiles(temp.Path, "state.json.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup, Encoding.UTF8));
    }

    [Fact]
    public void FutureSchemaStateIsBackedUpBeforeQuarantine()
    {
        using var temp = new TempDir();
        Write(
            temp,
            temp.Paths.StateFile,
            "{\"schemaVersion\":99,\"lastSnapshot\":{\"id\":\"future\"}}");

        var state = StateStore.Load(temp.Paths);

        Assert.Equal(1, state.SchemaVersion);
        Assert.Null(state.LastSnapshot);
        Assert.Single(Directory.GetFiles(temp.Path, "state.json.bak-*"));
        Assert.Single(Directory.GetFiles(temp.Path, "state.json.corrupt-*"));
    }

    [Fact]
    public void CorruptConfigIsBackedUpBeforeQuarantine()
    {
        using var temp = new TempDir();
        var original = Write(temp, temp.Paths.ConfigFile, "{ broken");

        var config = ConfigStore.Load(temp.Paths);

        Assert.Equal(1, config.SchemaVersion);
        var backup = Assert.Single(Directory.GetFiles(temp.Path, "config.json.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup, Encoding.UTF8));
        Assert.Single(Directory.GetFiles(temp.Path, "config.json.corrupt-*"));
    }

    [Fact]
    public void CorruptStateIsBackedUpBeforeQuarantine()
    {
        using var temp = new TempDir();
        var original = Write(temp, temp.Paths.StateFile, "{ broken");

        var state = StateStore.Load(temp.Paths);

        Assert.Equal(1, state.SchemaVersion);
        var backup = Assert.Single(Directory.GetFiles(temp.Path, "state.json.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup, Encoding.UTF8));
        Assert.Single(Directory.GetFiles(temp.Path, "state.json.corrupt-*"));
    }
}
