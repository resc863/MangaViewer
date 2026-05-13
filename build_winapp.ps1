param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'MangaViewer\MangaViewer.csproj'

dotnet restore $project
dotnet build $project `
    -c $Configuration `
    -f net10.0-windows10.0.26100.0 `
    -p:Platform=$Platform
