using Loom.Core.Environment;
using Loom.Core.Models;

namespace Loom.Core.Recovery;

/// <summary>
/// DSH CLI 的最小注入缝（包级恢复执行器只依赖它，单测可用假实现）。
/// 只暴露 <see cref="RunPluginAsync"/>，与二期 Upgrade 侧的同形端口
/// （<c>Loom.Core.Upgrade.IDshCli</c>）保持一致：一个适配器可以同时实现两个接口。
/// 方法签名与既有 <see cref="Loom.Core.Environment.DshCli"/> 完全一致：
/// 集成方可以直接让 DshCli 实现本接口（一行改动），或使用 <see cref="DshCliAdapter"/> 适配，
/// 两种方式都不需要改 Recovery 代码。
/// </summary>
public interface IDshCli
{
    /// <summary>
    /// 执行 <c>dsh plugin --profile &lt;profile&gt; &lt;args...&gt;</c>。
    /// 调用方不得持有 profile\package.json.lock（CLI 自己会加锁）。
    /// </summary>
    Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 适配器：把既有 <see cref="DshCli"/> 挂到 <see cref="IDshCli"/> 上（不改 Environment\DshCli.cs）。
/// 组合根里写成 <c>IDshCli cli = new DshCliAdapter(new DshCli(logger));</c> 即可；
/// 若同时需要 Upgrade 侧端口，可让同一个适配器类实现两个同名形状的接口。
/// </summary>
public sealed class DshCliAdapter : IDshCli
{
    private readonly DshCli _inner;

    public DshCliAdapter(DshCli inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<CliCallResult> RunPluginAsync(
        DshInstallation installation,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _inner.RunPluginAsync(installation, args, timeout, cancellationToken);
}
