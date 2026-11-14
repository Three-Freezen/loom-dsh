using System.Text;
using Loom.Core.Abstractions;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Loom.Core.Recovery;
using Loom.Core.Snapshot;
using Loom.Tests.CoreFlows;

namespace Loom.Tests.Recovery;

/// <summary>
/// 假 DSH CLI：只记录参数数组并返回测试指定的结果，绝不启动进程、绝不访问网络。
/// 包级恢复/离线回滚的测试都通过它注入成功与各类失败。
/// </summary>
internal sealed class FakeDshCli : IDshCli
{
    internal sealed class Call
    {
        public List<string> Args { get; init; } = new();
        public TimeSpan Timeout { get; init; }
        public string ProfileDir { get; init; } = "";
        public CliCallResult? Result { get; set; }
    }

    public List<Call> Calls { get; } = new();

    /// <summary>按调用返回结果；未设置时返回 exit=0。</summary>
    public Func<Call, CliCallResult>? Responder { get; set; }

    public Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Record(installation, args, timeout));

    private CliCallResult Record(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout)
    {
        var call = new Call
        {
            Args = args?.ToList() ?? new List<string>(),
            Timeout = timeout,
            ProfileDir = installation?.ProfileDir ?? "",
        };
        Calls.Add(call);
        var result = Responder?.Invoke(call)
                     ?? new CliCallResult { ExitCode = 0, CommandLine = "fake-dsh-cli" };
        call.Result = result;
        return result;
    }
}

/// <summary>包级恢复测试夹具：临时 sandbox（%TEMP%）、假 CLI、快照与计划构造。</summary>
internal static class RecoveryTestSupport
{
    internal static readonly ILogger Logger = new NoopLogger();

    internal static readonly Dictionary<string, string> Specs = new(StringComparer.Ordinal)
    {
        ["@deepseek-ai/dsh-base"] = "0.2.2-rc.1",
        ["@deepseek-ai/dsh-web-app"] = "0.2.2-rc.1",
        ["dshmarket"] = "^1.73.19",
        ["dsh-whale-widget"] = "^0.4.2",
    };

    internal static readonly Dictionary<string, string> Versions = new(StringComparer.Ordinal)
    {
        ["@deepseek-ai/dsh-base"] = "0.2.2-rc.1",
        ["@deepseek-ai/dsh-web-app"] = "0.2.2-rc.1",
        ["dshmarket"] = "1.73.19",
        ["dsh-whale-widget"] = "0.4.2",
    };

    internal static readonly string[] Bundles =
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
        "dshmarket",
        "dsh-whale-widget",
    };

    /// <summary>写入完整的 profile（package.json + pnpm-lock.yaml + node_modules + patch）。</summary>
    internal static void SeedProfile(LoomSandbox sandbox)
    {
        sandbox.WriteProfileFile(DshDefaults.ManifestFile, TestData.PackageJson(Bundles, Specs));
        sandbox.WriteProfileFile(DshDefaults.LockfileFile, TestData.Lockfile(Versions, Specs));
        sandbox.WritePatch(TestData.PatchYaml());
        foreach (var pair in Versions) sandbox.AddPlugin(pair.Key, pair.Value);
    }

    /// <summary>模拟一次“恢复流程清空 bundles/依赖”的漂移状态（值与原快照不同）。</summary>
    internal static void ApplyDrift(LoomSandbox sandbox, string marker = "drift")
    {
        sandbox.WriteProfileFile(
            DshDefaults.ManifestFile,
            TestData.PackageJson(
                new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["dshmarket"] = "^1.73.19" }));
        sandbox.WriteProfileFile(DshDefaults.LockfileFile, "lockfileVersion: '9.0'\n# " + marker + "\n");
    }

    internal static async Task<(SnapshotManifest Manifest, SnapshotRef Reference)> TakeBaselineAsync(LoomSandbox sandbox)
    {
        var service = new SnapshotService(sandbox.Paths, Logger);
        var manifest = await service.CaptureAsync(sandbox.Installation, SnapshotTrigger.PreUpdate, false);
        var reference = await service.WriteAsync(manifest, sandbox.Paths, new LoomConfig());
        return (manifest, reference);
    }

    /// <summary>构造 planner 纯输入：读取快照 profile 文件文本 + 当前 profile 解析结果。</summary>
    internal static PackageRestorePlanInput BuildInput(
        LoomSandbox sandbox,
        SnapshotManifest manifest,
        string snapshotDir,
        PackageRestoreOptions? options = null,
        string operationId = "op-recovery-1")
    {
        var snapshotProfile = Path.Combine(snapshotDir, "profile");
        var current = new ProfileReader(Logger).Read(sandbox.Installation, strict: false);
        return new PackageRestorePlanInput
        {
            Snapshot = manifest,
            SnapshotDir = snapshotDir,
            SnapshotPackageJson = ReadIfExists(Path.Combine(snapshotProfile, DshDefaults.ManifestFile)),
            SnapshotLockfile = ReadIfExists(Path.Combine(snapshotProfile, DshDefaults.LockfileFile)),
            Current = current,
            Installation = sandbox.Installation,
            Config = new LoomConfig(),
            Options = options ?? new PackageRestoreOptions(),
            OperationId = operationId,
        };
    }

    internal static PackageRestoreRequest Request(
        LoomSandbox sandbox,
        PackageRestorePlan plan,
        string? operationId = null,
        bool dryRun = false,
        bool appRunning = false) => new()
    {
        Installation = sandbox.Installation,
        Paths = sandbox.Paths,
        Plan = plan,
        OperationId = operationId ?? plan.OperationId,
        DryRun = dryRun,
        AppRunning = appRunning,
    };

    internal static string ReadIfExists(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";

    internal static string ReadProfile(LoomSandbox sandbox, string fileName) =>
        sandbox.ReadProfileFile(fileName);

    internal static string ShaOf(string path) => Hashing.Sha256HexOrDefault(path);

    internal static string JournalText(LoomSandbox sandbox, string operationId) =>
        string.Join("\n", JournalWriter.ReadLines(sandbox.Paths, operationId));

    internal static OfflineRollbackPoint? ReadPoint(LoomSandbox sandbox, string operationId) =>
        AtomicFile.ReadJson<OfflineRollbackPoint>(
            Path.Combine(sandbox.Paths.RestoreBackupDirFor(operationId), "package-restore.json"));

    /// <summary>取计划里的安装参数，便于断言“只用 --offline”。</summary>
    internal static List<string> InstallArgs(PackageRestorePlan plan) =>
        plan.Find(PackageRestoreStepKind.InstallOffline)?.Args.ToList() ?? new List<string>();
}
