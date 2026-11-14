using System.Reflection;
using System.Xml.Linq;
using Loom.Core.Models;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// UI 层回归（v0.1.1 曾因 TwoWay 绑定只读属性导致设置窗口打不开）：
/// 1) 反射加载构建产物里的 <c>Loom.ViewModels.SettingsViewModel</c>，验证保存映射
///    （<c>BuildUpdatedConfig</c> 是 <c>SaveAsync</c> 的唯一映射入口）把二期新增字段全部透传；
/// 2) 解析 <c>SettingsView.xaml</c>，逐个检查 TwoWay / 默认 TwoWay 的绑定目标属性都有 public setter。
///
/// 说明：测试项目不引用 WPF 工程（沙箱会拒绝 workload resolver），因此走 Assembly.LoadFrom；
/// 也不直接调用 SaveAsync / Load（内部会碰 WPF 主题资源），只验证与 WPF 无关的映射与元数据。
/// </summary>
public sealed class SettingsViewModelPassthroughTests
{
    [Fact]
    public void BuildUpdatedConfigCarriesAllUpgradeAndRecoveryFields()
    {
        var (viewModel, viewModelType, loom) = CreateViewModel();
        using (loom)
        {
            // 模拟已加载的旧配置（含界面不编辑的字段，保存后必须原样保留）。
            var original = new LoomConfig
            {
                AutoUpgradePolicy = "A",
                AllowMajorUpgrade = false,
                UpgradeQueryTimeoutSeconds = 123,
                UpgradeInstallTimeoutSeconds = 456,
                RequireAppExitForUpgrade = false,
                PackageRestoreInstallTimeoutSeconds = 789,
                PackageRestoreReadyTimeoutSeconds = 45,
                KeepRollbackBackups = 7,
                KeepSnapshots = 5,
                Theme = "system",
            };
            SetField(viewModel, "_original", original);
            SetField(viewModel, "_autoUpgradePolicy", "A");

            // 模拟用户在“更新与兼容”页打开开关。
            Set(viewModel, "AutoUpgradeEnabled", true);
            Set(viewModel, "AllowMajorUpgrade", true);
            Set(viewModel, "AllowPrereleaseUpgrade", true);
            Set(viewModel, "PackageRestoreEnabled", true);
            Set(viewModel, "PackageRestoreAllowNetworkFallback", true);

            var updated = InvokeBuildUpdatedConfig(viewModel, viewModelType, keepSnapshots: 10, quotaGb: 2.0);

            // 开关类字段来自界面
            Assert.True(updated.AutoUpgradeEnabled);
            Assert.True(updated.AllowMajorUpgrade);
            Assert.True(updated.AllowPrereleaseUpgrade);
            Assert.True(updated.PackageRestoreEnabled);
            Assert.True(updated.PackageRestoreAllowNetworkFallback);
            Assert.Equal("A", updated.AutoUpgradePolicy);

            // 非界面字段必须原样保留（不能因为保存被重置成默认值）
            Assert.Equal(123, updated.UpgradeQueryTimeoutSeconds);
            Assert.Equal(456, updated.UpgradeInstallTimeoutSeconds);
            Assert.False(updated.RequireAppExitForUpgrade);
            Assert.Equal(789, updated.PackageRestoreInstallTimeoutSeconds);
            Assert.Equal(45, updated.PackageRestoreReadyTimeoutSeconds);
            Assert.Equal(7, updated.KeepRollbackBackups);

            // 既有字段仍然生效
            Assert.Equal(10, updated.KeepSnapshots);
            Assert.Equal((long)(2.0 * 1024 * 1024 * 1024), updated.MaxSnapshotBytes);
        }
    }

    [Fact]
    public void RollbackStateFollowsStatusAndBackupAvailability()
    {
        var (viewModel, viewModelType, loom) = CreateViewModel();
        using (loom)
        {
            var refresh = viewModelType.GetMethod(
                "RefreshRollbackState",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            // 没有回滚点：按钮禁用、提示“暂无可回滚”。
            refresh.Invoke(viewModel, new object?[] { new LoomStatus() });
            Assert.False((bool)Get(viewModel, "CanRollbackPackage")!);
            Assert.Contains("暂无可回滚", (string)Get(viewModel, "RollbackHintText")!);

            // 有 opId，但 CanRollbackPackageOp=false（备份缺失/损坏）→ 仍然禁用。
            loom.CanRollback = false;
            refresh.Invoke(viewModel, new object?[] { new LoomStatus { RollbackOperationId = "op-x" } });
            Assert.False((bool)Get(viewModel, "CanRollbackPackage")!);

            // 有 opId 且回滚点可用（哈希校验通过）→ 启用并显示 opId。
            loom.CanRollback = true;
            refresh.Invoke(viewModel, new object?[] { new LoomStatus { RollbackOperationId = "20260101-000000-recovery-abcdef" } });
            Assert.True((bool)Get(viewModel, "CanRollbackPackage")!);
            Assert.Equal("20260101-000000-recovery-abcdef", (string)Get(viewModel, "LastRollbackOpId")!);
            Assert.Contains("20260101-000000-recovery-abcdef", (string)Get(viewModel, "RollbackHintText")!);
        }
    }

    [Fact]
    public void SettingsXamlTwoWayBindingsTargetSettableProperties()
    {
        var viewModelType = CreateViewModel().Type;

        var testDirectory = Path.GetDirectoryName(typeof(SettingsViewModelPassthroughTests).Assembly.Location)!;
        var xamlPath = Path.GetFullPath(Path.Combine(
            testDirectory,
            "..", "..", "..", "..",
            "Loom", "Views", "SettingsView.xaml"));
        Assert.True(File.Exists(xamlPath), "未找到 SettingsView.xaml：" + xamlPath);

        var text = File.ReadAllText(xamlPath);

        // 二期新增绑定必须真的接在“更新与兼容”页上（避免只改 VM 不改 XAML）。
        foreach (var expected in new[]
                 {
                     "AutoUpgradeEnabled", "AllowMajorUpgrade", "AllowPrereleaseUpgrade",
                     "PackageRestoreEnabled", "PackageRestoreAllowNetworkFallback",
                     "RollbackPackageCommand", "CanRollbackPackage",
                 })
        {
            Assert.Contains("Binding " + expected, text);
        }

        var document = XDocument.Load(xamlPath);
        var checkedBindings = 0;
        foreach (var element in document.Descendants())
        {
            var elementName = element.Name.LocalName;
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value.Trim();
                if (!value.StartsWith("{Binding", StringComparison.Ordinal)) continue;

                var mode = ExtractMode(value);
                var isTwoWay = string.Equals(mode, "TwoWay", StringComparison.OrdinalIgnoreCase)
                               || (mode is null && IsTwoWayByDefault(elementName, attribute.Name.LocalName));
                if (!isTwoWay) continue;

                var path = ExtractPath(value);
                if (path.Length == 0) continue;
                var propertyName = path.Split('.')[0];
                var property = viewModelType.GetProperty(propertyName);
                Assert.True(
                    property is not null,
                    $"XAML 绑定了 SettingsViewModel 上不存在的属性：{propertyName}（<{elementName} {attribute.Name.LocalName}>）");
                Assert.True(
                    property!.SetMethod is not null && property.SetMethod.IsPublic,
                    $"TwoWay/默认 TwoWay 绑定必须指向可写属性：{propertyName}（v0.1.1 设置窗口打不开的原因）");
                checkedBindings++;
            }
        }

        Assert.True(checkedBindings > 0, "没有检查到任何 TwoWay 绑定（XAML 结构变化？）");
    }

    /// <summary>WPF 里这些“用户可编辑”的控件属性默认就是 TwoWay；其余（如 TextBlock.Text）默认 OneWay。</summary>
    private static bool IsTwoWayByDefault(string elementName, string attributeName) =>
        (elementName.ToLowerInvariant(), attributeName.ToLowerInvariant()) switch
        {
            ("togglebutton" or "checkbox" or "radiobutton", "ischecked") => true,
            ("textbox", "text") => true,
            ("passwordbox", "password") => true,
            ("combobox" or "listbox", "selecteditem") => true,
            ("datepicker", "selecteddate") => true,
            ("slider", "value") => true,
            _ => false,
        };

    // ------------------------------------------------------------ reflection helpers

    private static (object ViewModel, Type Type, FakeLoomService Loom) CreateViewModel()
    {
        var testDirectory = Path.GetDirectoryName(typeof(SettingsViewModelPassthroughTests).Assembly.Location)!;
        var assemblyPath = Path.GetFullPath(Path.Combine(
            testDirectory,
            "..", "..", "..", "..",
            "Loom", "bin", "Debug", "net10.0-windows", "win-x64", "loom-dsh.dll"));
        Assert.True(
            File.Exists(assemblyPath),
            "未找到 Loom 构建产物（应先运行 scripts\\build.ps1 构建 src\\Loom）：" + assemblyPath);

        var assembly = Assembly.LoadFrom(assemblyPath);
        var type = assembly.GetType("Loom.ViewModels.SettingsViewModel", throwOnError: true)!;
        var loom = new FakeLoomService();
        var viewModel = Activator.CreateInstance(type, loom)!;
        return (viewModel, type, loom);
    }

    private static LoomConfig InvokeBuildUpdatedConfig(object viewModel, Type type, int keepSnapshots, double quotaGb)
    {
        var build = type.GetMethod("BuildUpdatedConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(build is not null, "SettingsViewModel 缺少 BuildUpdatedConfig（SaveAsync 的映射入口）");
        return (LoomConfig)build!.Invoke(viewModel, new object?[] { keepSnapshots, quotaGb })!;
    }

    private static void Set(object target, string property, object value)
    {
        var info = target.GetType().GetProperty(property)
                   ?? throw new InvalidOperationException("缺少属性：" + property);
        Assert.True(info.SetMethod is not null, "属性没有 public setter（会重演 v0.1.1 的 TwoWay 绑定崩溃）：" + property);
        info.SetValue(target, value);
    }

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property)?.GetValue(target);

    private static void SetField(object target, string field, object value)
    {
        var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(info is not null, "缺少字段：" + field);
        info!.SetValue(target, value);
    }

    private static string? ExtractMode(string binding)
    {
        foreach (var part in binding.Trim('{', '}').Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Mode=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Mode=".Length..].Trim();
            }
        }

        return null;
    }

    private static string ExtractPath(string binding)
    {
        var body = binding.Trim();
        body = body["{Binding".Length..].Trim();
        if (body.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) body = body["Path=".Length..];
        var end = body.IndexOfAny(new[] { ',', '}' });
        return (end >= 0 ? body[..end] : body).Trim();
    }
}
