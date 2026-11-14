using System.Reflection;
using Loom.Core.Cli;
using Loom.Core.Composition;
using Xunit;

namespace Loom.Tests.Infrastructure;

/// <summary>
/// 托盘队列执行器（Loom.AppServices）的参数错误退出码（P2-7）：必须与直接 CLI 一致为 Failed(1)。
/// 测试项目不引用 WPF 工程（沙箱的 workload resolver 会拒绝），这里在运行期从构建产物反射加载
/// loom-dsh.dll 并只调用不涉及窗口的 ExecuteCliAsync；数据目录指向 %TEMP%。
/// </summary>
public sealed class AppServicesReflectionTests
{
    [Fact]
    public async Task ExecuteCliAsyncReturnsFailedForArgumentError()
    {
        var testDirectory = Path.GetDirectoryName(typeof(AppServicesReflectionTests).Assembly.Location)!;
        var loomAssemblyPath = Path.GetFullPath(Path.Combine(
            testDirectory,
            "..", "..", "..", "..",
            "Loom", "bin", "Debug", "net10.0-windows", "win-x64", "loom-dsh.dll"));
        Assert.True(
            File.Exists(loomAssemblyPath),
            "未找到 Loom 构建产物（应先运行 scripts\\build.ps1 构建 src\\Loom）：" + loomAssemblyPath);

        var assembly = Assembly.LoadFrom(loomAssemblyPath);
        var appServicesType = assembly.GetType("Loom.AppServices", throwOnError: true)!;
        var dataRoot = Path.Combine(Path.GetTempPath(), "loom-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = (IDisposable)Activator.CreateInstance(
                appServicesType,
                new LoomOptions { DataRoot = dataRoot })!;
            var execute = appServicesType.GetMethod("ExecuteCliAsync")!;
            var task = (Task<Loom.Core.Ipc.CliProcessResult>)execute.Invoke(
                services,
                new object?[] { new[] { "status", "--definitely-not-an-option" }, CancellationToken.None })!;

            var result = await task;

            Assert.Equal(ExitCodes.Failed, result.ExitCode);
            Assert.NotEqual(ExitCodes.Busy, result.ExitCode);
            Assert.Contains("未知参数", result.StdErr);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
