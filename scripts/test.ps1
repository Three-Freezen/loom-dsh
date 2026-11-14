# Loom for DSH - run the xunit test suite.
# Uses `dotnet test` when the environment allows the VSTest testhost; falls back to the bundled
# in-process runner (src\Loom.TestRunner) for restricted sandboxes that deny testhost process handles.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\test.ps1 [-ForceRunner]
param([switch]$ForceRunner)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
Push-Location $src
try {
    $testsDll = Join-Path $src 'Loom.Tests\bin\Debug\net10.0-windows\Loom.Tests.dll'
    if (-not (Test-Path $testsDll)) { throw "Test assembly missing. Build first: $testsDll" }

    if (-not $ForceRunner) {
        Write-Host '== dotnet test =='
        dotnet test Loom.Tests\Loom.Tests.csproj -c Debug --no-build -v q
        if ($LASTEXITCODE -eq 0) { Write-Host 'TESTS OK (dotnet test)'; return }
        Write-Host "dotnet test unavailable (exit $LASTEXITCODE); falling back to Loom.TestRunner"
    }

    $runner = Join-Path $src 'Loom.TestRunner\bin\Debug\net10.0-windows\Loom.TestRunner.dll'
    if (-not (Test-Path $runner)) {
        dotnet build Loom.TestRunner\Loom.TestRunner.csproj -c Debug
        if ($LASTEXITCODE -ne 0) { throw "runner build failed ($LASTEXITCODE)" }
    }

    Write-Host '== Loom.TestRunner =='
    dotnet $runner $testsDll
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
    Write-Host 'TESTS OK (in-process runner)'
}
finally {
    Pop-Location
}
