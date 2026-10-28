[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$BenchmarkArgs
)

. (Join-Path $PSScriptRoot 'common.ps1')
$project = Join-Path $LiteCleanRoot 'tests\LiteClean.Tests\LiteClean.Tests.csproj'
$resultFile = Join-Path $Artifacts 'benchmark-results.txt'

Push-Location $LiteCleanRoot
try {
    & dotnet build $project -c Release --nologo -v:minimal -m:1 /nodeReuse:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $arguments = @('--benchmark') + $BenchmarkArgs
    $output = & dotnet run --project $project -c Release --no-build -- @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath $resultFile
    Write-Host ""
    Write-Host "Benchmark results saved to: $resultFile"
    exit $exitCode
}
finally {
    Pop-Location
}
