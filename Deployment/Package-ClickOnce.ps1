<#
.SYNOPSIS
  ClickOnce-style filesystem publish for Albert Retail Terminal (HTTPS deployment share).
#>
param(
    [string] $Configuration = 'Release',
    [string] $ProductVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $repoRoot 'src\PointOfSale.App\PointOfSale.App.csproj'
$outDir = Join-Path $repoRoot 'publish\ClickOnce'

Write-Host "=== ClickOnce publish ($ProductVersion) ==="

dotnet publish $appProject `
    -c $Configuration `
    /p:PublishProfile=ClickOnceProfile `
    /p:Version=$ProductVersion `
    /p:AssemblyVersion="$ProductVersion.0" `
    /p:FileVersion="$ProductVersion.0"

if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}
Move-Item (Join-Path $repoRoot 'publish\ClickOnce') $outDir -Force -ErrorAction SilentlyContinue
if (-not (Test-Path $outDir)) {
    $outDir = Join-Path $repoRoot 'publish\ClickOnce'
}

$setupExe = Get-ChildItem -Path $outDir -Filter 'setup.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
Write-Host "Publish complete. Host folder on HTTPS: $outDir"
if ($setupExe) {
    Write-Host "Bootstrapper: $($setupExe.FullName)"
}
