[CmdletBinding()]
param(
    [switch]$SkipBenchmark
)

. (Join-Path $PSScriptRoot 'common.ps1')
$scriptFailed = $false

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host ""
    Write-Host "=== $Name ==="
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        $script:scriptFailed = $true
    }
    else {
        Write-Host "OK: $Name" -ForegroundColor Green
    }
}

Invoke-Step 'Build' { & (Join-Path $PSScriptRoot 'build.ps1') }
Invoke-Step 'Tests' { & (Join-Path $PSScriptRoot 'test.ps1') }
if (-not $SkipBenchmark) {
    Invoke-Step 'Benchmark' { & (Join-Path $PSScriptRoot 'benchmark.ps1') }
}
Invoke-Step 'Publish + smoke test' { & (Join-Path $PSScriptRoot 'publish.ps1') }

if ($scriptFailed) {
    Write-Host ""
    Write-Host "VERIFY RESULT: FAIL" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "VERIFY RESULT: PASS" -ForegroundColor Green
exit 0
