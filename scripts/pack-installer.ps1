# Loom for DSH - Inno Setup 安装包打包脚本
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\pack-installer.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\pack-installer.ps1 -Version 0.1.1
#   powershell -ExecutionPolicy Bypass -File scripts\pack-installer.ps1 -SkipPublish
#
# 默认流程: scripts\publish.ps1 发布单文件 exe -> Inno Setup 编译 installer\loom-dsh.iss
#           -> 产出 dist\loom-dsh-Setup.exe + 更新 dist\SHA256SUMS.txt / dist\发布说明.txt
# 沙箱测试产物: -SkipPublish -SandboxTest -> .tmp-installer\loom-dsh-Setup-test.exe
#           （DSH 沙箱拒绝注册表/%APPDATA% 写入，测试变体不注册卸载项、图标落安装目录内；
#            仅用于受限环境端到端验证安装/升级/卸载，不进 SHA256SUMS/发布说明，不随发布分发）
# 依赖: Inno Setup 6（ISCC.exe；本机 %LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe）
# 产物: dist\loom-dsh-Setup.exe —— 固定文件名（不带版本号），供 releases/latest/download
#       直链与未来应用内更新使用。版本号通过 ISCC /DAppVersion=<x.y.z> 注入。
param(
    [string]$Version = '',
    [string]$OutputDir = '',
    [string]$Configuration = 'Release',
    [string]$IsccPath = '',
    [switch]$SkipPublish,
    [switch]$SandboxTest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$project = Join-Path $src 'Loom\Loom.csproj'
$iss = Join-Path $root 'installer\loom-dsh.iss'
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    if ($SandboxTest) { $OutputDir = Join-Path $root '.tmp-installer' }
    else { $OutputDir = Join-Path $root 'dist' }
}
$outputBaseName = 'loom-dsh-Setup'
if ($SandboxTest) { $outputBaseName = 'loom-dsh-Setup-test' }

Write-Host '== Loom for DSH installer pack =='
Write-Host "root    : $root"
Write-Host "output  : $OutputDir"
if ($SandboxTest) { Write-Host 'mode    : sandbox test build (no uninstall registry key, icons inside {app})' }

# ── 1) 版本号：参数 > Loom.csproj <Version> > 0.1.0（单一版本来源，与 publish 一致）──
if ([string]::IsNullOrWhiteSpace($Version)) {
    if (Test-Path $project) {
        [xml]$csproj = Get-Content $project -Raw -Encoding UTF8
        $Version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } |
                    Where-Object { $_ } | Select-Object -First 1)
    }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '0.1.0' }
}
Write-Host "version : $Version"

# ── 2) 发布（可跳过；publish.ps1 -Installer 会传 -SkipPublish 避免递归）──
if (-not $SkipPublish) {
    $publish = Join-Path $PSScriptRoot 'publish.ps1'
    if (-not (Test-Path $publish)) { throw "publish.ps1 不存在: $publish" }
    powershell -NoProfile -ExecutionPolicy Bypass -File $publish -OutputDir $OutputDir -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 失败（退出码 $LASTEXITCODE）" }
}

# 发布产物（loom-dsh.exe）固定从 dist 读取；沙箱测试变体的输出目录只放安装包。
$exe = Join-Path $root 'dist\loom-dsh.exe'
if (-not $SandboxTest) { $exe = Join-Path $OutputDir 'loom-dsh.exe' }
if (-not (Test-Path $exe)) { throw "发布产物不存在: $exe（请先运行 scripts\publish.ps1）" }

# ── 3) 定位 ISCC.exe ──
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path $IsccPath)) {
    throw '未找到 Inno Setup 6 的 ISCC.exe，请先安装: winget install JRSoftware.InnoSetup'
}
Write-Host "iscc    : $IsccPath"

if (-not (Test-Path $iss)) { throw "安装脚本不存在: $iss" }

# ── 4) 编译安装包（AppVersion/主程序路径/输出目录由 /D 定义注入）──
if ($SandboxTest) {
    & $IsccPath "/DAppVersion=$Version" "/DMyOutputDir=$OutputDir" "/DMyOutputBaseFilename=$outputBaseName" "/DMyExePath=$exe" '/DTestSandbox=1' $iss
} else {
    & $IsccPath "/DAppVersion=$Version" "/DMyOutputDir=$OutputDir" "/DMyOutputBaseFilename=$outputBaseName" "/DMyExePath=$exe" $iss
}
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）" }

$setup = Join-Path $OutputDir "$outputBaseName.exe"
if (-not (Test-Path $setup)) { throw "安装包未生成: $setup" }

# 沙箱测试产物到此为止：不进 SHA256SUMS / 发布说明（不是发布物）。
if ($SandboxTest) {
    $setupHash = (Get-FileHash -Algorithm SHA256 -Path $setup).Hash.ToLowerInvariant()
    $setupSize = (Get-Item $setup).Length
    Write-Host ''
    Write-Host "OK (sandbox test build): $setup"
    Write-Host ("SHA256: {0}" -f $setupHash)
    Write-Host ("Size  : {0:N0} bytes" -f $setupSize)
    return
}

# ── 5) 更新 SHA256SUMS.txt（保留 exe 行，替换/追加安装包行）──
$exeHash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash.ToLowerInvariant()
$setupHash = (Get-FileHash -Algorithm SHA256 -Path $setup).Hash.ToLowerInvariant()
$sumsPath = Join-Path $OutputDir 'SHA256SUMS.txt'
$lines = @()
if (Test-Path $sumsPath) {
    $lines = @(Get-Content $sumsPath | Where-Object { $_ -and ($_ -notmatch 'loom-dsh-Setup\.exe') })
}
$lines = @($lines | Where-Object { $_ -notmatch 'loom-dsh\.exe\s*$' })
$lines += "$exeHash  loom-dsh.exe"
$lines += "$setupHash  loom-dsh-Setup.exe"
Set-Content -Path $sumsPath -Value $lines -Encoding ASCII

# ── 6) 发布说明补充安装包信息（幂等：已含 loom-dsh-Setup.exe 则不重复追加）──
$readmePath = Join-Path $OutputDir '发布说明.txt'
$readme = ''
if (Test-Path $readmePath) { $readme = Get-Content $readmePath -Raw -Encoding UTF8 }
if ($readme -notmatch 'loom-dsh-Setup\.exe') {
    # 仅追加安装包专属段落；数据目录/迁移说明由 publish.ps1 的发布说明模板负责，
    # 避免两条链路各写一遍造成重复。此分支只在该说明文件不是由 publish.ps1 生成时兜底。
    $block = @"

- 安装包：loom-dsh-Setup.exe（Inno Setup 6，免管理员；固定文件名便于应用内更新）
- 安装包 SHA256：{0}
- 安装（交互）：双击安装包；默认装到 %LOCALAPPDATA%\Programs\Loom，向导可改路径
- 静默升级（未来应用内更新同款）：loom-dsh-Setup.exe /SILENT /DIR="<当前安装目录>"，装完自动重启应用
- 卸载：设置 -> 应用 -> Loom for DSH，或运行 <安装目录>\unins000.exe；用户数据目录 Data 不会被删除
"@ -f $setupHash
    $readme = $readme.TrimEnd() + $block
    Set-Content -Path $readmePath -Value $readme -Encoding UTF8
} else {
    # 已有安装包段落但哈希可能来自旧构建：替换安装包 SHA256 行。
    $readme = [regex]::Replace($readme, '(?m)^- 安装包 SHA256：.*$', "- 安装包 SHA256：$setupHash")
    Set-Content -Path $readmePath -Value $readme -Encoding UTF8
}

$setupSize = (Get-Item $setup).Length
Write-Host ''
Write-Host "OK: $setup"
Write-Host ("SHA256: {0}" -f $setupHash)
Write-Host ("Size  : {0:N0} bytes" -f $setupSize)
Write-Host "Sums  : $sumsPath"
Write-Host "Notes : $readmePath"
