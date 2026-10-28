[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TestArgs
)

. (Join-Path $PSScriptRoot 'common.ps1')
$project = Join-Path $LiteCleanRoot 'tests\LiteClean.Tests\LiteClean.Tests.csproj'
$resultFile = Join-Path $Artifacts 'test-results.txt'

Push-Location $LiteCleanRoot
try {
    & dotnet build $project -c Release --nologo -v:minimal -m:1 /nodeReuse:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $output = & dotnet run --project $project -c Release --no-build -- @TestArgs 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath $resultFile
    Write-Host ""
    Write-Host "Test results saved to: $resultFile"
    exit $exitCode
}
finally {
    Pop-Location
}
