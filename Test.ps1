param([int]$Seconds = 600, [string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 10) { throw 'Use at least 10 seconds; acceptance requires 600 seconds.' }
Push-Location $PSScriptRoot
$previousDotnetRoot = $env:DOTNET_ROOT
try {
    $dotnetExecutable = (Get-Command $DotnetPath -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path -Parent $dotnetExecutable
    & $DotnetPath restore .\ScreenTranslator.sln --configfile .\NuGet.Config --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $DotnetPath build .\Tests\ScreenTranslator.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $resultDirectory = Join-Path $PSScriptRoot ('artifacts\test-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    & .\Tests\bin\Release\net10.0-windows10.0.19041.0\ScreenTranslator.Tests.exe $Seconds $resultDirectory
    if ($LASTEXITCODE -ne 0) { throw "Test failed. Inspect $resultDirectory" }
    Write-Host "Passed. Results: $resultDirectory"
}
finally { $env:DOTNET_ROOT = $previousDotnetRoot; Pop-Location }
