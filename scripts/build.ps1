# Loom for DSH - build + test helpers
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
Push-Location $src
try {
    # Build projects individually: the DSH sandbox cannot build the .slnx graph (workload SDK
    # resolver + VSTest testhost are blocked), while individual projects build fine.
    dotnet build Loom.Core\Loom.Core.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Loom.Core build failed ($LASTEXITCODE)" }

    dotnet build Loom.Tests\Loom.Tests.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Loom.Tests build failed ($LASTEXITCODE)" }

    dotnet build Loom\Loom.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Loom build failed ($LASTEXITCODE)" }

    dotnet build Loom.TestRunner\Loom.TestRunner.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Loom.TestRunner build failed ($LASTEXITCODE)" }

    powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1')
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }

    Write-Host 'BUILD + TESTS OK'
}
finally {
    Pop-Location
}
