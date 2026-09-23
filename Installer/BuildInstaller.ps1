param(
    [string]$DotnetPath = "",
    [string]$IsccPath = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$stagingRoot = Join-Path $repoRoot 'artifacts\installer'
$payloadDirectory = Join-Path $stagingRoot 'payload'
$outputDirectory = Join-Path $stagingRoot 'output'
$scriptPath = Join-Path $PSScriptRoot 'ScreenTranslator.iss'

function Assert-UnderProject([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the project: $fullPath"
    }
}

Assert-UnderProject $payloadDirectory
Assert-UnderProject $outputDirectory

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $localDotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        (Join-Path $repoRoot 'tools\InnoSetup\ISCC.exe'),
        (Join-Path $repoRoot 'tools\InnoSetup\ISCC\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Inno Setup compiler was not found. Install Inno Setup 6 or pass -IsccPath.'
}

if (Test-Path -LiteralPath $payloadDirectory) {
    $resolvedPayload = (Resolve-Path -LiteralPath $payloadDirectory).Path
    Assert-UnderProject $resolvedPayload
    if ((Split-Path -Leaf $resolvedPayload) -ne 'payload') { throw "Unexpected staging path: $resolvedPayload" }
    Remove-Item -LiteralPath $resolvedPayload -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Push-Location $repoRoot
try {
    & $DotnetPath build '.\ScreenTranslator.sln' -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

    & $DotnetPath publish '.\App\ScreenTranslator.csproj' -c Release -r win-x64 --self-contained true `
        -o $payloadDirectory --no-restore -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

    $licensesDirectory = Join-Path $payloadDirectory 'licenses'
    New-Item -ItemType Directory -Path $licensesDirectory -Force | Out-Null
    Copy-Item -LiteralPath '.\docs\ThirdPartyNotices.md' -Destination $licensesDirectory -Force
    Copy-Item -Path '.\docs\licenses\*' -Destination $licensesDirectory -Force

    [xml]$project = Get-Content -LiteralPath '.\App\ScreenTranslator.csproj'
    $version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw 'App version was not found in ScreenTranslator.csproj.' }

    & $IsccPath "/DAppVersion=$version" "/DPayloadDir=$payloadDirectory" "/DOutputDir=$outputDirectory" $scriptPath
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

    $installer = Join-Path $outputDirectory "ScreenTranslator-Setup-$version-win-x64.exe"
    if (-not (Test-Path -LiteralPath $installer)) { throw "Installer output is missing: $installer" }
    Write-Host "Installer ready: $installer"
}
finally {
    Pop-Location
}
