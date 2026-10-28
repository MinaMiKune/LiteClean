[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

. (Join-Path $PSScriptRoot 'common.ps1')
$solution = Join-Path $LiteCleanRoot 'LiteClean.sln'

Push-Location $LiteCleanRoot
try {
    & dotnet build $solution -c $Configuration --nologo -v:minimal -m:1 /nodeReuse:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
