namespace Loom.Core.Cli;

/// <summary>Exit codes are part of the CLI contract (design section 16.1).</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int NeedsManual = 2;
    public const int Unavailable = 3;
    public const int Busy = 4;
}

public sealed class CliInvocation
{
    public string Command { get; set; } = "status";
    public List<string> Positionals { get; set; } = new();
    public bool Json { get; set; }
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public bool Quiet { get; set; }
    public bool Help { get; set; }
    public bool Tray { get; set; }
    public bool Yes { get; set; }

    /// <summary>migrate-data: delete the old data root after a successful migration.</summary>
    public bool RemoveSource { get; set; }

    /// <summary>update: download the setup asset after a successful version check.</summary>
    public bool UpdateDownload { get; set; }

    /// <summary>update: launch the downloaded installer silently (Loom must exit to be replaced).</summary>
    public bool UpdateApply { get; set; }

    /// <summary>update: local directory containing loom-dsh-Setup.exe (offline test source).</summary>
    public string? UpdateSource { get; set; }

    /// <summary>update: override the download destination directory.</summary>
    public string? UpdateDownloadDir { get; set; }

    /// <summary>upgrade: 只处理指定插件包名（缺省 = 按检查结果批量处理不兼容项）。</summary>
    public string? PackageName { get; set; }

    /// <summary>upgrade: 允许把 prerelease 候选纳入决策（仍稳定版优先）。</summary>
    public bool AllowPrerelease { get; set; }

    public string? DataRoot { get; set; }
    public string? InstallDir { get; set; }
    public string? DshHome { get; set; }
    public string? ProfileDir { get; set; }
    public string? LogLevel { get; set; }
    public string? SnapshotId { get; set; }
    public string? UiTarget { get; set; }
    public string? Error { get; set; }
    public bool CommandWasExplicit { get; set; }

    public bool IsUi => string.Equals(Command, "ui", StringComparison.OrdinalIgnoreCase);

    public bool RoutesThroughTray =>
        !Help
        && !string.Equals(Command, "version", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Command, "help", StringComparison.OrdinalIgnoreCase);
}

public static class CliParser
{
    public static CliInvocation Parse(string[] args)
    {
        var invocation = new CliInvocation();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    invocation.Json = true;
                    continue;
                case "--dry-run":
                case "--dryrun":
                    invocation.DryRun = true;
                    continue;
                case "--force":
                case "-f":
                    invocation.Force = true;
                    continue;
                case "--quiet":
                case "-q":
                    invocation.Quiet = true;
                    continue;
                case "--yes":
                case "-y":
                    invocation.Yes = true;
                    continue;
                case "--help":
                case "-h":
                case "-?":
                    invocation.Help = true;
                    continue;
                case "--tray":
                    invocation.Tray = true;
                    continue;
                case "--remove-source":
                    invocation.RemoveSource = true;
                    continue;
                case "--download":
                    invocation.UpdateDownload = true;
                    continue;
                case "--apply":
                    invocation.UpdateApply = true;
                    continue;
                case "--allow-prerelease":
                    invocation.AllowPrerelease = true;
                    continue;
            }

            if (arg is "--pkg" or "--package")
            {
                if (++index >= args.Length) return Error(invocation, "--pkg 缺少参数值");
                invocation.PackageName = args[index];
                continue;
            }

            if (arg is "--update-source")
            {
                if (++index >= args.Length) return Error(invocation, "--update-source 缺少参数值");
                invocation.UpdateSource = args[index];
                continue;
            }

            if (arg is "--download-dir")
            {
                if (++index >= args.Length) return Error(invocation, "--download-dir 缺少参数值");
                invocation.UpdateDownloadDir = args[index];
                continue;
            }

            if (arg is "--data-root" or "--data")
            {
                if (++index >= args.Length) return Error(invocation, "--data-root 缺少参数值");
                invocation.DataRoot = args[index];
                continue;
            }

            if (arg is "--install-dir")
            {
                if (++index >= args.Length) return Error(invocation, "--install-dir 缺少参数值");
                invocation.InstallDir = args[index];
                continue;
            }

            if (arg is "--dsh-home")
            {
                if (++index >= args.Length) return Error(invocation, "--dsh-home 缺少参数值");
                invocation.DshHome = args[index];
                continue;
            }

            if (arg is "--profile-dir")
            {
                if (++index >= args.Length) return Error(invocation, "--profile-dir 缺少参数值");
                invocation.ProfileDir = args[index];
                continue;
            }

            if (arg is "--log-level")
            {
                if (++index >= args.Length) return Error(invocation, "--log-level 缺少参数值");
                invocation.LogLevel = args[index];
                continue;
            }

            if (arg is "--snapshot")
            {
                if (++index >= args.Length) return Error(invocation, "--snapshot 缺少参数值");
                invocation.SnapshotId = args[index];
                continue;
            }

            if (arg is "--ui")
            {
                if (++index >= args.Length) return Error(invocation, "--ui 缺少参数值");
                invocation.UiTarget = args[index];
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return Error(invocation, $"未知参数：{arg}");
            }

            if (!invocation.CommandWasExplicit)
            {
                invocation.Command = arg;
                invocation.CommandWasExplicit = true;
            }
            else
            {
                invocation.Positionals.Add(arg);
            }
        }

        if (invocation.IsUi && string.IsNullOrEmpty(invocation.UiTarget))
        {
            invocation.UiTarget = invocation.Positionals.FirstOrDefault() ?? "popup";
        }

        return invocation;
    }

    private static CliInvocation Error(CliInvocation invocation, string message)
    {
        invocation.Error = message;
        return invocation;
    }
}
