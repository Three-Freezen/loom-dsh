# Loom for DSH - sandbox acceptance checks (scope A, design section 16.3)
# Creates throw-away DSH_HOME/install-dir copies under the repo workspace; never touches the real profile.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\acceptance-sandbox.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\Loom\bin\Debug\net10.0-windows\win-x64\loom-dsh.exe'
if (-not (Test-Path $exe)) { throw "Build first: $exe" }

$realProfile = Join-Path $env:USERPROFILE '.dsh\profiles\desktop'
if (-not (Test-Path (Join-Path $realProfile 'package.json'))) { throw "Real profile not found: $realProfile" }

$results = New-Object System.Collections.Generic.List[object]
function Record([string]$id, [string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Id = $id; Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1} {2} - {3}" -f $mark, $id, $name, $detail)
}
function Invoke-Loom([string[]]$argList) {
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo.FileName = $exe
    $p.StartInfo.Arguments = ($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.RedirectStandardOutput = $true
    $p.StartInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $p.StartInfo.RedirectStandardError = $true
    $p.StartInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $p.StartInfo.CreateNoWindow = $true
    [void]$p.Start()
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return [pscustomobject]@{ Exit = $p.ExitCode; Out = $out; Err = $err }
}
function Sha([string]$path) { if (Test-Path $path) { (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant() } else { '' } }

# ---------------------------------------------------------------- sandbox
$root = Join-Path $repo ('.acceptance\' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
$dshHome = Join-Path $root 'dsh home 测试'      # A24: non-ASCII + space in DSH_HOME
$profile = Join-Path $dshHome 'profiles\desktop'
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force -Path $profile, $data | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $install 'resources\runtime\primary-runtime') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $install 'resources\runtime\cli\bin') | Out-Null
Set-Content -Encoding ASCII -Path (Join-Path $install 'resources\runtime\primary-runtime\runtime.json') -Value '{"desktopVersion":"0.2.2-rc.1","platform":"win32","arch":"x64"}'
Set-Content -Encoding ASCII -Path (Join-Path $install 'resources\runtime\cli\bin\dsh.cmd') -Value '@echo off`r`nexit /b 0'
Set-Content -Encoding ASCII -Path (Join-Path $install 'resources\app.asar') -Value 'stub'
Set-Content -Encoding ASCII -Path (Join-Path $install 'resources\app-update.yml') -Value 'provider: generic'

Copy-Item (Join-Path $realProfile 'package.json') $profile -Force
Copy-Item (Join-Path $realProfile 'pnpm-lock.yaml') $profile -Force
Copy-Item (Join-Path $realProfile 'cordis.patch.yml') $profile -Force
$realCompatibility = Join-Path $realProfile 'compatibility.json'
if (Test-Path $realCompatibility) { Copy-Item $realCompatibility $profile -Force }
$manifest = Get-Content (Join-Path $profile 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$dependencies = @($manifest.dependencies.PSObject.Properties.Name)
foreach ($dep in $dependencies) {
    $source = Join-Path $realProfile "node_modules\$dep\package.json"
    $target = Join-Path $profile "node_modules\$dep\package.json"
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    if (Test-Path $source) { Copy-Item $source $target -Force } else { Set-Content -Encoding UTF8 -Path $target -Value ('{"name":"' + $dep + '","version":"0.0.0"}') }
}
$baseArgs = @('--install-dir', $install, '--dsh-home', $dshHome, '--data-root', $data)
$originalBundles = @($manifest.dsh.profile.bundles)
$originalPatch = Get-Content (Join-Path $profile 'cordis.patch.yml') -Raw -Encoding UTF8
$installHashes = @{}
Get-ChildItem $install -Recurse -File | ForEach-Object { $installHashes[$_.FullName] = Sha $_.FullName }

# Short operation timeout so the lock-busy case fails fast instead of waiting a full minute.
Set-Content -Encoding ASCII -Path (Join-Path $data 'config.json') -Value '{"schemaVersion":1,"operationTimeoutSeconds":3}'

# ---------------------------------------------------------------- A01 snapshot
$snapshot = Invoke-Loom (@('snapshot', '--json') + $baseArgs)
$snapOk = $snapshot.Exit -eq 0 -and $snapshot.Out -match '"ok": true'
$snapJson = $null
try { $snapJson = $snapshot.Out | ConvertFrom-Json } catch {}
$snapDir = if ($snapJson) { $snapJson.snapshot.path } else { '' }
$snapFilesOk = $snapDir -and (Test-Path (Join-Path $snapDir 'snapshot.json')) -and (Test-Path (Join-Path $snapDir 'profile\package.json')) -and (Test-Path (Join-Path $snapDir 'profile\pnpm-lock.yaml'))
$installUnchanged = $true
Get-ChildItem $install -Recurse -File | ForEach-Object { if ($installHashes[$_.FullName] -ne (Sha $_.FullName)) { $installUnchanged = $false } }
Record 'A01' 'baseline snapshot' ($snapOk -and $snapFilesOk -and $installUnchanged) "exit=$($snapshot.Exit) snapshot=$snapDir installUnchanged=$installUnchanged"

# ---------------------------------------------------------------- A26 missing install
$missing = Invoke-Loom @('status', '--json', '--install-dir', (Join-Path $root 'nope'), '--dsh-home', $dshHome, '--data-root', $data)
# A26 is only fully reproducible on a VM without DSH: an invalid explicit install dir falls back
# to the running-process / registry candidates by design, so only assert a graceful status here.
Record 'A26' 'missing installation (manual on clean VM)' ($missing.Exit -eq 0) "exit=$($missing.Exit)"

# ---------------------------------------------------------------- A17 lock busy
Set-Content -Encoding ASCII -Path (Join-Path $profile 'lock') -Value ([string]$PID)
$busyArgs = @('snapshot', '--json') + $baseArgs
$busy = Invoke-Loom $busyArgs
Remove-Item (Join-Path $profile 'lock') -Force
$busyOk = $busy.Exit -eq 4
Record 'A17' 'profile lock respected' $busyOk "exit=$($busy.Exit) err=$($busy.Err.Trim())"

# ---------------------------------------------------------------- A06/A14 recovery simulation
$manifest = Get-Content (Join-Path $profile 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.dsh.profile.bundles = @('@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app')
$manifest | Add-Member -NotePropertyName 'loomUserField' -NotePropertyValue 'keep-me' -Force
$manifest.dependencies | Add-Member -NotePropertyName 'user-extra-plugin' -NotePropertyValue '^1.0.0' -Force
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 -Path (Join-Path $profile 'package.json')
$bak = Join-Path $profile 'cordis.patch.yml.bak-20261114-120000'
Move-Item (Join-Path $profile 'cordis.patch.yml') $bak -Force
$check = Invoke-Loom (@('check', '--json') + $baseArgs)
$checkJson = $null
try { $checkJson = $check.Out | ConvertFrom-Json } catch {}
$evidenceStrong = $checkJson -and $checkJson.result.evidence -eq 'strong'
$restorePossible = $checkJson -and $checkJson.result.restorePossible -eq $true
$removed = if ($checkJson) { @($checkJson.result.removedBundles) } else { @() }
Record 'A06' 'recovery evidence detected' ($evidenceStrong -and $restorePossible) ("exit=$($check.Exit) evidence=" + $(if ($checkJson) { $checkJson.result.evidence } else { '?' }) + " removed=" + ($removed -join ','))

$restore = Invoke-Loom (@('restore', '--force', '--json') + $baseArgs)
$restoreJson = $null
try { $restoreJson = $restore.Out | ConvertFrom-Json } catch {}
$afterManifest = Get-Content (Join-Path $profile 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$bundlesAfter = @($afterManifest.dsh.profile.bundles)
$bundlesOk = ($bundlesAfter -join '|') -eq ($originalBundles -join '|')
$userFieldOk = $afterManifest.loomUserField -eq 'keep-me'
$userDepOk = $null -ne $afterManifest.dependencies.'user-extra-plugin'
$patchRestored = (Test-Path (Join-Path $profile 'cordis.patch.yml')) -and ((Get-Content (Join-Path $profile 'cordis.patch.yml') -Raw -Encoding UTF8).Trim() -eq $originalPatch.Trim())
$restoreOk = $restore.Exit -eq 0 -and $restoreJson -and $restoreJson.outcome -eq 'Succeeded'
Record 'A14' 'file-level restore after recovery' ($restoreOk -and $bundlesOk -and $patchRestored -and $userFieldOk -and $userDepOk) ("exit=$($restore.Exit) bundlesRestored=$bundlesOk patchRestored=$patchRestored userFieldKept=$userFieldOk userDepKept=$userDepOk")
Record 'A15' 'user changes preserved' ($userFieldOk -and $userDepOk) ("userFieldKept=$userFieldOk userDepKept=$userDepOk")

$again = Invoke-Loom (@('restore', '--force', '--json') + $baseArgs)
$againJson = $null
try { $againJson = $again.Out | ConvertFrom-Json } catch {}
$idempotent = $again.Exit -eq 0 -and $againJson -and ($againJson.restored -eq 0 -or $againJson.plan.hasChanges -eq $false)
Record 'A14b' 'restore idempotent' $idempotent "exit=$($again.Exit) restored=$($againJson.restored)"

# ---------------------------------------------------------------- A23 corrupt compatibility.json not overwritten
$compatPath = Join-Path $profile 'compatibility.json'
$corrupt = '{ this is not json'
Set-Content -Encoding UTF8 -Path $compatPath -Value $corrupt
$restore3 = Invoke-Loom (@('restore', '--force', '--json') + $baseArgs)
$compatAfter = Get-Content $compatPath -Raw -Encoding UTF8
$compatOk = $compatAfter.Trim() -eq $corrupt.Trim()
Record 'A23' 'corrupt compatibility.json preserved' $compatOk "exit=$($restore3.Exit)"

# ---------------------------------------------------------------- A16 empty data root
$emptyData = Join-Path $root 'empty-data'
$noSnapshot = Invoke-Loom @('restore', '--json', '--install-dir', $install, '--dsh-home', $dshHome, '--data-root', $emptyData)
$noSnapshotOk = $noSnapshot.Exit -ne 0 -and ($noSnapshot.Out -match '没有可用的快照' -or $noSnapshot.Err -match '没有可用的快照')
Record 'A16' 'no snapshot is not faked' $noSnapshotOk "exit=$($noSnapshot.Exit)"

# ---------------------------------------------------------------- summary
Write-Host ''
$passed = @($results | Where-Object Ok).Count
$failed = @($results | Where-Object { -not $_.Ok }).Count
Write-Host ("ACCEPTANCE: {0} passed, {1} failed (sandbox: {2})" -f $passed, $failed, $root)
$results | Format-Table -AutoSize | Out-String | Write-Host
if ($failed -gt 0) { exit 1 }
exit 0
