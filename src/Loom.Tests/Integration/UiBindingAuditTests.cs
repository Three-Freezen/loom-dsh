using System.Reflection;
using System.Xml.Linq;
using Loom.Core.Infrastructure;
using Loom.Core.Models;
using Xunit;

namespace Loom.Tests.Integration;

/// <summary>
/// P3 回归：
/// 1) 首启向导的 <c>CloneConfig</c> 必须保留全部配置字段（旧实现手写拷贝，二期新增字段会被重置）；
/// 2) 全库默认 TwoWay 绑定审计（v0.1.1 曾因 TwoWay 绑定只读属性导致设置窗口打不开）——覆盖
///    SettingsView 之外的 ReportView / FirstRunView / TrayPopupView / TrayMenuView 与 Themes，
///    要求目标属性有 public setter，或绑定显式写了 <c>Mode=OneWay</c>。
/// </summary>
public sealed class UiBindingAuditTests
{
    [Fact]
    public void FirstRunCloneConfigCarriesEveryConfiguredField()
    {
        var (type, assemblyPath) = LoadLoomType("Loom.ViewModels.FirstRunViewModel");
        var cloneMethod = type.GetMethod(
            "CloneConfig",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(cloneMethod is not null, "FirstRunViewModel 缺少 CloneConfig：" + assemblyPath);

        var source = new LoomConfig
        {
            // 二期新增（旧的手写拷贝全部漏掉）
            AutoUpgradeEnabled = true,
            AllowMajorUpgrade = true,
            AllowPrereleaseUpgrade = true,
            UpgradeQueryTimeoutSeconds = 31,
            UpgradeInstallTimeoutSeconds = 32,
            RequireAppExitForUpgrade = false,
            PackageRestoreEnabled = true,
            PackageRestoreAllowNetworkFallback = true,
            PackageRestoreInstallTimeoutSeconds = 33,
            PackageRestoreReadyTimeoutSeconds = 34,
            KeepRollbackBackups = 9,
            // 应用内更新（旧实现也漏掉）
            AutoCheckUpdate = false,
            DeleteSetupAfterUpdate = false,
            LastUpdateCheckUtc = new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.Zero),
            // 既有字段
            InstallDir = @"D:\DSH",
            DshHome = @"D:\DSH\home",
            ProfileDir = @"D:\DSH\home\profiles\desktop",
            DataRoot = @"D:\LoomData",
            Theme = "dark",
            LogLevel = "debug",
            KeepSnapshots = 7,
            MaxSnapshotBytes = 123456789,
            MaxArchiveBytes = 987654321,
            QuietHoursStart = "22:30",
            QuietHoursEnd = "07:30",
            Notifications = false,
            Autostart = true,
            FirstRunCompleted = true,
        };

        var clone = (LoomConfig)cloneMethod!.Invoke(null, new object[] { source })!;

        // 用 JSON 快照对比：任何字段丢失/重置都会在这里暴露。
        Assert.Equal(JsonDefaults.Serialize(source), JsonDefaults.Serialize(clone));
        Assert.NotSame(source, clone);
    }

    [Theory]
    [InlineData("SettingsView.xaml", "Loom.ViewModels.SettingsViewModel", true)]
    [InlineData("FirstRunView.xaml", "Loom.ViewModels.FirstRunViewModel", true)]
    [InlineData("ReportView.xaml", "Loom.ViewModels.ReportViewModel", false)]
    [InlineData("TrayPopupView.xaml", "Loom.ViewModels.TrayPopupViewModel", false)]
    [InlineData("TrayMenuView.xaml", "Loom.ViewModels.TrayMenuViewModel", false)]
    public void ViewTwoWayBindingsTargetSettableProperties(
        string xamlFile,
        string viewModelTypeName,
        bool expectTwoWayBindings)
    {
        var (type, _) = LoadLoomType(viewModelTypeName);
        var xamlPath = Path.GetFullPath(Path.Combine(LoomSourceDirectory(), "Views", xamlFile));
        Assert.True(File.Exists(xamlPath), "未找到 XAML：" + xamlPath);

        AssertTwoWayBindingsAreSettable(xamlPath, type, expectTwoWayBindings);
    }

    [Fact]
    public void ThemesTwoWayBindingsAreExplicitOneWay()
    {
        var themeDirectory = Path.Combine(LoomSourceDirectory(), "Themes");
        Assert.True(Directory.Exists(themeDirectory), "未找到 Themes 目录：" + themeDirectory);

        foreach (var xamlPath in Directory.GetFiles(themeDirectory, "*.xaml"))
        {
            var document = XDocument.Load(xamlPath);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var value = attribute.Value.Trim();
                    if (!value.StartsWith("{Binding", StringComparison.Ordinal)) continue;
                    var mode = ExtractMode(value);
                    var isTwoWay = string.Equals(mode, "TwoWay", StringComparison.OrdinalIgnoreCase)
                                   || (mode is null && IsTwoWayByDefault(element.Name.LocalName, attribute.Name.LocalName));
                    if (!isTwoWay) continue;

                    // 模板内的 TemplatedParent 绑定目标是模板父级（有 setter），审计白名单。
                    if (value.Contains("TemplatedParent", StringComparison.OrdinalIgnoreCase)) continue;
                    Assert.True(
                        string.Equals(mode, "OneWay", StringComparison.OrdinalIgnoreCase),
                        $"{Path.GetFileName(xamlPath)}：<{element.Name.LocalName} {attribute.Name.LocalName}> "
                        + $"的 TwoWay 绑定需要显式 Mode=OneWay 或指向可写属性：{value}");
                }
            }
        }
    }

    // ------------------------------------------------------------ helpers

    private static void AssertTwoWayBindingsAreSettable(
        string xamlPath,
        Type viewModelType,
        bool expectTwoWayBindings)
    {
        var document = XDocument.Load(xamlPath);
        var allBindings = 0;
        var checkedBindings = 0;
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value.Trim();
                if (!value.StartsWith("{Binding", StringComparison.Ordinal)) continue;
                allBindings++;

                var mode = ExtractMode(value);
                var isTwoWay = string.Equals(mode, "TwoWay", StringComparison.OrdinalIgnoreCase)
                               || (mode is null && IsTwoWayByDefault(element.Name.LocalName, attribute.Name.LocalName));
                if (!isTwoWay) continue;

                var path = ExtractPath(value);
                if (path.Length == 0) continue;
                var propertyName = path.Split('.', '/')[0];
                var property = viewModelType.GetProperty(propertyName);
                Assert.True(
                    property is not null,
                    $"{Path.GetFileName(xamlPath)} 绑定了 {viewModelType.Name} 上不存在的属性：{propertyName}");
                Assert.True(
                    property!.SetMethod is not null && property.SetMethod.IsPublic,
                    $"{Path.GetFileName(xamlPath)}：TwoWay/默认 TwoWay 绑定必须指向 public setter 属性，"
                    + $"否则窗口会打不开（v0.1.1 回归）：{propertyName}");
                checkedBindings++;
            }
        }

        // 只读视图（报告/托盘弹层/托盘菜单）可能没有默认 TwoWay 控件：不强制 TwoWay 数量，
        // 但必须确认解析到了绑定，避免 XAML 结构变化让审计静默失效。
        Assert.True(allBindings > 0, Path.GetFileName(xamlPath) + " 没有检查到任何绑定（XAML 结构变化？）");
        if (expectTwoWayBindings)
        {
            Assert.True(checkedBindings > 0, Path.GetFileName(xamlPath) + " 没有检查到 TwoWay 绑定（TwoWay 检测退化？）");
        }
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
            ("progressbar", "value") => true,
            _ => false,
        };

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

    internal static string LoomSourceDirectory()
    {
        var testDirectory = Path.GetDirectoryName(typeof(UiBindingAuditTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", "..", "Loom"));
    }

    internal static (Type Type, string AssemblyPath) LoadLoomType(string typeName)
    {
        var assemblyPath = Path.GetFullPath(Path.Combine(
            LoomSourceDirectory(), "bin", "Debug", "net10.0-windows", "win-x64", "loom-dsh.dll"));
        Assert.True(
            File.Exists(assemblyPath),
            "未找到 Loom 构建产物（应先运行 scripts\\build.ps1 构建 src\\Loom）：" + assemblyPath);
        var assembly = Assembly.LoadFrom(assemblyPath);
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return (type, assemblyPath);
    }
}
