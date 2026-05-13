param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',

    [switch]$NoBuild,

    [switch]$DebugOutput,

    [switch]$Clean,

    [switch]$Detach
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'MangaViewer\MangaViewer.csproj'
$manifest = Join-Path $PSScriptRoot 'MangaViewer\Package.appxmanifest'
$rid = switch ($Platform) {
    'x86' { 'win-x86' }
    'x64' { 'win-x64' }
    'ARM64' { 'win-arm64' }
}
$output = Join-Path $PSScriptRoot "MangaViewer\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0\$rid"

if (-not $NoBuild) {
    dotnet restore $project
    dotnet build $project `
        -c $Configuration `
        -f net10.0-windows10.0.26100.0 `
        -p:Platform=$Platform
}

$args = @(
    'run',
    $output,
    '--manifest',
    $manifest,
    '--exe',
    'MangaViewer.exe'
)

if ($DebugOutput) {
    $args += '--debug-output'
}

if ($Clean) {
    $args += '--clean'
}

if ($Detach) {
    $args += '--detach'
}

winapp @args
