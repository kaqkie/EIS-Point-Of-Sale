<#
.SYNOPSIS
  Publishes Albert Retail Terminal, optionally signs binaries, and builds the WiX MSI.
#>
param(
    [string] $Configuration = 'Release',
    [string] $ProductVersion = '1.0.0',
    [switch] $SkipPublish,
    [switch] $SkipSign,
    [switch] $SkipMsi,
    [switch] $ConfigureFirewall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $repoRoot 'publish\AlbertRetailTerminal'
$msiOutDir = Join-Path $repoRoot 'publish\Installer'
$appProject = Join-Path $repoRoot 'src\PointOfSale.App\PointOfSale.App.csproj'
$wixProject = Join-Path $PSScriptRoot 'AlbertRetailTerminal.wixproj'
# Phase 35 primary authoring file: ProductInstaller.wxs (compiled by the WiX SDK project)

Write-Host "=== Albert Retail Terminal — Installer build ($ProductVersion) ==="

if (-not $SkipPublish) {
    Write-Host "Publishing self-contained win-x64..."
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    dotnet publish $appProject `
        -c $Configuration `
        /p:PublishProfile=FolderProfile `
        /p:Version=$ProductVersion `
        /p:AssemblyVersion=$ProductVersion.0 `
        /p:FileVersion=$ProductVersion.0

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    # Ensure SQL scripts are beside the published app for first-launch bootstrap fallback
    $scriptsTarget = Join-Path $publishDir 'Scripts'
    New-Item -ItemType Directory -Force -Path $scriptsTarget | Out-Null
    Copy-Item (Join-Path $repoRoot 'Scripts\*.sql') $scriptsTarget -Force
}

if (-not $SkipSign) {
    & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -PublishDir $publishDir
}

if (-not $SkipMsi) {
    Write-Host "Building MSI with WiX..."
    New-Item -ItemType Directory -Force -Path $msiOutDir | Out-Null

    $publishDirArg = $publishDir.TrimEnd('\') + '\'
    $repoRootArg = $repoRoot.TrimEnd('\') + '\'

    dotnet build $wixProject `
        -c $Configuration `
        -p:ProductVersion=$ProductVersion `
        -p:PublishDir=$publishDirArg `
        -p:RepoRoot=$repoRootArg `
        -p:OutputPath=$msiOutDir\

    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI build failed. Install WiX Toolset SDK 5 (WixToolset.Sdk) and retry."
    }

    if (-not $SkipSign) {
        $msi = Get-ChildItem $msiOutDir -Filter *.msi | Select-Object -First 1
        if ($msi) {
            $env:ART_CODE_SIGN_CERT_PATH = $env:ART_CODE_SIGN_CERT_PATH
            & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -PublishDir $msiOutDir
        }
    }

    Write-Host "MSI output: $msiOutDir"
}

if ($ConfigureFirewall) {
    $installHint = if (Test-Path $publishDir) { $publishDir } else { "$env:ProgramFiles\Albert Retail Terminal" }
    & (Join-Path $PSScriptRoot 'ConfigureFirewall.ps1') -InstallDir $installHint
}

Write-Host "Done."
