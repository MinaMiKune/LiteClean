# Shared LiteClean build environment. Dot-sourced by the scripts in this folder.
$ErrorActionPreference = 'Stop'

$script:LiteCleanRoot = Split-Path -Parent $PSScriptRoot
$script:BuildHome = Join-Path $LiteCleanRoot '.build-home'
$script:BuildPackages = Join-Path $LiteCleanRoot '.build-packages'
$script:Artifacts = Join-Path $LiteCleanRoot 'artifacts'

# Keep all build state inside the project. This also makes builds work in
# restricted environments where the default user profile is not writable.
$env:DOTNET_CLI_HOME = $BuildHome
$env:NUGET_PACKAGES = $BuildPackages
$env:APPDATA = Join-Path $BuildHome 'AppData\Roaming'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'

foreach ($directory in @($BuildHome, $BuildPackages, $env:APPDATA, $Artifacts)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

function Assert-LastExitCode {
    param([string]$Operation)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}
