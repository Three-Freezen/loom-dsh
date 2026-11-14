# Loom for DSH - self-contained single-file publish (win-x64) + Inno Setup installer
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#     默认：发布 exe，并在找到 Inno Setup 6 时一并生成 dist\loom-dsh-Setup.exe
#           （找不到 ISCC 只警告、仍然产出绿色版 exe，不失败）
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -NoInstaller
#     只发布绿色版 exe（不生成安装包）
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Installer
#     -Installer 为兼容旧用法的别名（默认行为已包含安装包，无需传入）
param(
    [string]$OutputDir = '',
    [string]$Configuration = 'Release',
    [switch]$Installer,
    [switch]$NoInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $root 'dist' }
$src = Join-Path $root 'src'
$project = Join-Path $src 'Loom\Loom.csproj'
# 默认生成安装包（用户要求以 Inno Setup 安装方式分发）；-NoInstaller 才只要绿色版。
# -Installer 保留为兼容别名（显式传入也不改变默认行为）。
$withInstaller = -not $NoInstaller
if ($Installer) { $withInstaller = $true }

# 单一版本来源：Loom.csproj 的 <Version>（与安装包 /DAppVersion 一致）
$version = '0.2.0'
if (Test-Path $project) {
    [xml]$csproj = Get-Content $project -Raw -Encoding UTF8
    $fromCsproj = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } |
                   Where-Object { $_ } | Select-Object -First 1)
    if (-not [string]::IsNullOrWhiteSpace($fromCsproj)) { $version = $fromCsproj }
}

Write-Host "== Loom for DSH publish =="
Write-Host "version : $version"
Write-Host "src     : $src"
Write-Host "output  : $OutputDir"
Write-Host "config  : $Configuration"
if ($withInstaller) { Write-Host 'installer: yes (默认；-NoInstaller 可关闭)' }
else { Write-Host 'installer: no (-NoInstaller)' }

Push-Location $src
try {
    # Two-step restore/publish is required on offline build machines: the configured
    # NuGet mirror may be unreachable, while all needed packs are already cached.
    dotnet restore $project -r win-x64 /p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw "restore failed ($LASTEXITCODE)" }

    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutputDir | Out-Null

    dotnet publish $project -c $Configuration -r win-x64 --self-contained true --no-restore `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=false `
        /p:DebugType=none `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$exe = Join-Path $OutputDir 'loom-dsh.exe'
if (-not (Test-Path $exe)) { throw "publish output not found: $exe" }

$hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash.ToLowerInvariant()
$size = (Get-Item $exe).Length
"$hash  loom-dsh.exe" | Set-Content -Path (Join-Path $OutputDir 'SHA256SUMS.txt') -Encoding ASCII

# 发布说明基础段落；安装包相关（含安装包 SHA256、安装/静默升级/卸载命令）由
# scripts\pack-installer.ps1 生成安装包后追加，保证与实际产物一致。
$readmeTemplate = @"
Loom for DSH {2}

- 主程序：loom-dsh.exe（托盘 + CLI 同一个 exe，自包含 .NET 10，无需安装运行时）
- 文件大小：{0:N0} 字节
- SHA256：{1}
- 使用：
    loom-dsh.exe                     启动托盘
    loom-dsh.exe status              查看状态
    loom-dsh.exe snapshot            立即快照
    loom-dsh.exe check               检查兼容性
    loom-dsh.exe restore --dry-run   演练文件级还原
    loom-dsh.exe migrate-data        把旧数据目录迁移到当前数据根（--dry-run/--remove-source）
    loom-dsh.exe update              检查应用更新（--download 下载 / --apply 静默升级 / --json）
    loom-dsh.exe --help              完整帮助
- 数据目录（0.1.0 起）：
    * 安装版：<安装目录>\Data（Inno 安装后自动识别；卸载保留；首次启动自动迁移旧数据）
    * 绿色版 / 开发版：%LOCALAPPDATA%\Loom
    * 便携模式：<exe目录>\Data\portable.flag 存在时使用 <exe目录>\Data
    * 优先级：--data-root > 便携标记 > 安装目录\Data > %LOCALAPPDATA%\Loom
- 旧数据迁移：安装版首次启动把 %LOCALAPPDATA%\Loom 的设置/快照/报告/日志复制到 <安装目录>\Data
  （幂等、不覆盖已有文件、跳过 queue/临时/锁文件；自动重写 state.json/config.json 里的旧绝对路径；
  只复制不删除旧目录），成功后写 Data\migration.json。也可手动运行 loom-dsh migrate-data。
- 安全：不修改 DSH 安装目录与 app.asar；不自动结束 DSH 进程；不自动授予兼容性风险豁免。
- 应用内更新：安装版可在 设置 → 更新与兼容 → 应用更新 检查/下载/静默升级 GitHub Releases 新版本
  （固定资产名 loom-dsh-Setup.exe，/SILENT /DIR=当前安装目录，装完自动重启）；绿色版/开发版需手动下载。
  CLI：loom-dsh update [--download] [--apply] [--json]。启动后台检查默认 24 小时一次，可在设置关闭。
- 许可：MIT（见仓库 LICENSE）；公开仓库 https://github.com/Three-Freezen/loom-dsh
- 说明：未签名 exe 可能触发 SmartScreen，请核对 SHA256；代码签名在二期。
- 范围：快照 / 检查 / 文件级还原 / 报告 / 托盘与设置 / 安装包 / 数据迁移 / 应用内更新；
  策略 A 全自动升级、包级恢复、离线完整回滚、代码签名在后续版本。
- 数据目录 ACL 会尝试收紧为当前用户+SYSTEM；受限环境可能回退为继承权限，请勿分享数据目录。
"@
$readme = $readmeTemplate -f $size, $hash, $version
Set-Content -Path (Join-Path $OutputDir '发布说明.txt') -Value $readme -Encoding UTF8

Write-Host ''
Write-Host "OK: $exe"
Write-Host ("SHA256: {0}" -f $hash)
Write-Host ("Size  : {0:N0} bytes" -f $size)

# ── 安装包（默认生成；找不到 Inno Setup 时只警告不失败）──
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($withInstaller) {
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        Write-Warning '未找到 Inno Setup 6 的 ISCC.exe，跳过安装包（绿色版 exe 已正常产出）。安装: winget install JRSoftware.InnoSetup'
    } else {
        Write-Host ''
        Write-Host '== 生成 Inno Setup 安装包 =='
        $pack = Join-Path $PSScriptRoot 'pack-installer.ps1'
        powershell -NoProfile -ExecutionPolicy Bypass -File $pack -Version $version -OutputDir $OutputDir -Configuration $Configuration -SkipPublish
        if ($LASTEXITCODE -ne 0) { throw "pack-installer.ps1 failed ($LASTEXITCODE)" }
    }
} else {
    Write-Host ''
    Write-Host '提示：已指定 -NoInstaller，本次只发布绿色版 exe（安装包可用 scripts\pack-installer.ps1 单独生成）。'
}
