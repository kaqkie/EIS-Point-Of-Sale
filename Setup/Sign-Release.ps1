<#
.SYNOPSIS
  Authenticode-signs Albert Retail Terminal release binaries for enterprise rollout.
.DESCRIPTION
  Uses signtool.exe with a code-signing certificate. Configure via environment:
    ART_CODE_SIGN_CERT_PATH   — path to .pfx
    ART_CODE_SIGN_CERT_PASSWORD — certificate password (prefer secret store in CI)
    ART_CODE_SIGN_TIMESTAMP_URL — optional RFC3161 timestamp URL
.PARAMETER PublishDir
  Folder containing published binaries (default: publish\AlbertRetailTerminal)
.PARAMETER WhatIf
  Print actions without signing.
#>
param(
    [string] $PublishDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\AlbertRetailTerminal'),
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

$certPath = $env:ART_CODE_SIGN_CERT_PATH
$certPassword = $env:ART_CODE_SIGN_CERT_PASSWORD
$timestampUrl = if ($env:ART_CODE_SIGN_TIMESTAMP_URL) {
    $env:ART_CODE_SIGN_TIMESTAMP_URL
} else {
    'http://timestamp.digicert.com'
}

if ([string]::IsNullOrWhiteSpace($certPath) -or -not (Test-Path -LiteralPath $certPath)) {
    Write-Warning "ART_CODE_SIGN_CERT_PATH is not set or file missing. Skipping Authenticode signing."
    Write-Host "Set ART_CODE_SIGN_CERT_PATH and ART_CODE_SIGN_CERT_PASSWORD to enable signing."
    exit 0
}

$signtool = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\signtool.exe"
) | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    throw "signtool.exe not found. Install the Windows 10/11 SDK Signing Tools."
}

$targets = @()
$targets += Get-ChildItem -Path $PublishDir -Include *.exe, *.dll -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'AlbertRetailTerminal|PointOfSale' -or $_.Extension -eq '.exe' }
$targets += Get-ChildItem -Path $PublishDir -Filter *.msi -ErrorAction SilentlyContinue
$targets = $targets | Sort-Object FullName -Unique

if (-not $targets) {
    throw "No binaries found under $PublishDir"
}

Write-Host "Signing $($targets.Count) file(s) with $certPath using $signtool"

foreach ($file in $targets) {
    $args = @(
        'sign',
        '/fd', 'SHA256',
        '/td', 'SHA256',
        '/tr', $timestampUrl,
        '/f', $certPath
    )
    if (-not [string]::IsNullOrWhiteSpace($certPassword)) {
        $args += @('/p', $certPassword)
    }
    $args += $file.FullName

    if ($WhatIf) {
        Write-Host "WHATIF: signtool $($args -join ' ')"
        continue
    }

    & $signtool @args
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $($file.FullName) (exit $LASTEXITCODE)"
    }
}

Write-Host "Authenticode signing complete."
