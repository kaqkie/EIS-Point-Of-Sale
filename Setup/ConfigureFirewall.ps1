#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Configures Windows Firewall outbound rules for Albert Retail Terminal (MRA EIS HTTPS).
.PARAMETER InstallDir
  Installation directory containing AlbertRetailTerminal.exe
.PARAMETER Remove
  Remove rules instead of creating them.
#>
param(
    [Parameter(Mandatory = $false)]
    [string] $InstallDir = "$env:ProgramFiles\Albert Retail Terminal",

    [switch] $Remove
)

$ErrorActionPreference = 'Stop'
$ruleName = 'Albert Retail Terminal — MRA EIS HTTPS'
$exePath = Join-Path $InstallDir 'AlbertRetailTerminal.exe'

if ($Remove) {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Removed firewall rule: $ruleName"
    exit 0
}

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Warning "Executable not found at $exePath — skipping firewall rule."
    exit 0
}

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Outbound `
    -Action Allow `
    -Program $exePath `
    -Protocol TCP `
    -RemotePort 443 `
    -Profile Any `
    -Description 'Allow Albert Retail Terminal outbound HTTPS to MRA EIS sandbox/production APIs.' | Out-Null

# Optional inbound block for the POS process (defense in depth — no listener expected)
$inboundName = 'Albert Retail Terminal — Block Unsolicited Inbound'
Get-NetFirewallRule -DisplayName $inboundName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $inboundName `
    -Direction Inbound `
    -Action Block `
    -Program $exePath `
    -Protocol Any `
    -Profile Any `
    -Description 'Block unsolicited inbound connections to the POS executable.' | Out-Null

Write-Host "Firewall rules configured for $exePath"
