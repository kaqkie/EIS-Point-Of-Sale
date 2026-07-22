<#
.SYNOPSIS
  Publishes Albert Retail Terminal and packages a sideload MSIX using MakeAppx (Windows SDK).
#>
param(
    [string] $Configuration = 'Release',
    [string] $ProductVersion = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $repoRoot 'publish\MsixStaging'
$layoutDir = Join-Path $publishDir 'Layout'
$msixOut = Join-Path $repoRoot "publish\Msix\AlbertRetailTerminal_$ProductVersion`_x64.msix"
$appProject = Join-Path $repoRoot 'src\PointOfSale.App\PointOfSale.App.csproj'
$manifestTemplate = Join-Path $PSScriptRoot 'Msix\AppxManifest.xml'

Write-Host "=== MSIX packaging ($ProductVersion) ==="

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path $msixOut) | Out-Null

dotnet publish $appProject `
    -c $Configuration `
    /p:PublishProfile=FolderProfile `
    /p:Version=$ProductVersion

if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$published = Join-Path $repoRoot 'publish\AlbertRetailTerminal'
if (-not (Test-Path $published)) {
    throw "Expected publish folder: $published"
}

Copy-Item $published $layoutDir -Recurse -Force
Copy-Item $manifestTemplate (Join-Path $layoutDir 'AppxManifest.xml') -Force

$makeAppx = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\makeappx.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64\makeappx.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $makeAppx) {
    throw 'MakeAppx.exe not found. Install Windows 10/11 SDK.'
}

& $makeAppx pack /d $layoutDir /p $msixOut /o
Write-Host "MSIX written to $msixOut"
Write-Host "Sign with: signtool sign /fd SHA256 /a $msixOut"
