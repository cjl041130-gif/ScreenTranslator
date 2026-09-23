param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $DotnetPath restore .\ScreenTranslator.sln --configfile .\NuGet.Config --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $DotnetPath build .\ScreenTranslator.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $releaseDirectory = Join-Path $PSScriptRoot 'Release'
    if (Test-Path -LiteralPath $releaseDirectory) {
        $resolvedRelease = (Resolve-Path -LiteralPath $releaseDirectory).Path
        if ((Split-Path -Parent $resolvedRelease) -ne $PSScriptRoot -or (Split-Path -Leaf $resolvedRelease) -ne 'Release') { throw 'Refusing to clean an unexpected publish path.' }
        Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
    }
    & $DotnetPath publish .\App\ScreenTranslator.csproj -c Release -r win-x64 --self-contained true -o .\Release --configfile .\NuGet.Config -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $licensesDirectory = Join-Path $PSScriptRoot 'Release\licenses'
    New-Item -ItemType Directory -Force $licensesDirectory | Out-Null
    Copy-Item -LiteralPath .\docs\ThirdPartyNotices.md -Destination $licensesDirectory -Force
    Copy-Item .\docs\licenses\* -Destination $licensesDirectory -Force
    Write-Host 'Ready: Release\ScreenTranslator.exe'
}
finally { Pop-Location }
