[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
$project = Join-Path $LiteCleanRoot 'src\LiteClean.App\LiteClean.App.csproj'
$outputDirectory = Join-Path $Artifacts 'publish\LiteClean-win-x64-framework-dependent'
$zipPath = Join-Path $Artifacts 'LiteClean-1.0.0-win-x64-framework-dependent.zip'

if (Test-Path $outputDirectory) {
    Remove-Item -Recurse -Force $outputDirectory
}
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

Push-Location $LiteCleanRoot
try {
    # Framework-dependent portable publish. It works offline with the installed
    # .NET 10 SDK and produces a small folder. Self-contained/single-file modes
    # require NuGet runtime packs that are not available in this environment.
    & dotnet publish $project `
        -c Release `
        --self-contained false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:GenerateDocumentationFile=false `
        -o $outputDirectory `
        --nologo `
        -v:minimal `
        -m:1 `
        /nodeReuse:false `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Copy-Item -Path (Join-Path $LiteCleanRoot 'README.md') -Destination (Join-Path $outputDirectory 'README.md') -Force
    Copy-Item -Path (Join-Path $LiteCleanRoot 'docs') -Destination $outputDirectory -Recurse -Force

    $smoke = Start-Process -FilePath (Join-Path $outputDirectory 'LiteClean.exe') `
        -ArgumentList '--smoke-test' `
        -PassThru `
        -Wait
    if ($smoke.ExitCode -ne 0) {
        throw "Published LiteClean.exe smoke test failed with exit code $($smoke.ExitCode)."
    }

    $manifestPath = Join-Path $outputDirectory 'SHA256SUMS.txt'
    if (Test-Path $manifestPath) {
        Remove-Item -Force $manifestPath
    }

    $manifestLines = Get-ChildItem -File $outputDirectory -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($outputDirectory, $_.FullName)
            $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    Set-Content -Path $manifestPath -Value $manifestLines -Encoding utf8

    if (Test-Path $zipPath) {
        Remove-Item -Force $zipPath
    }
    Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $exeHash = (Get-FileHash -Path (Join-Path $outputDirectory 'LiteClean.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $externalManifest = Join-Path $Artifacts 'SHA256SUMS.txt'
    @(
        "LiteClean 1.0.0 win-x64 framework-dependent"
        "exe sha256: $exeHash"
        "zip sha256: $zipHash"
        "zip bytes: $((Get-Item $zipPath).Length)"
        "published-utc: $([DateTime]::UtcNow.ToString('O'))"
    ) | Set-Content -Path $externalManifest -Encoding utf8

    $files = Get-ChildItem -File $outputDirectory -Recurse
    $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    $zipBytes = (Get-Item $zipPath).Length
    Write-Host ""
    Write-Host "Published: $outputDirectory"
    Write-Host "Files: $($files.Count), total: $totalBytes bytes ($([math]::Round($totalBytes / 1KB, 1)) KB)"
    Write-Host "Zip: $zipPath ($zipBytes bytes, $([math]::Round($zipBytes / 1KB, 1)) KB)"
    Write-Host "Exe SHA256: $exeHash"
    Write-Host "Zip SHA256: $zipHash"
    Write-Host "Smoke test: exit code 0"
    $files | Select-Object FullName, Length | Format-Table -AutoSize
}
finally {
    Pop-Location
}
