<#
.SYNOPSIS
  Detects SQL Server Express (SQLEXPRESS) or LocalDB and prepares the PointOfSale database.

.DESCRIPTION
  Phase 35 retail-counter helper. Prefer named instance SQLEXPRESS; fall back to
  (localdb)\MSSQLLocalDB when Express is unavailable (typical for thin POS laptops).

.NOTES
  Run elevated when creating LocalDB instances or installing Express media.
#>
param(
    [string] $InstanceName = 'SQLEXPRESS',
    [string] $DatabaseName = 'PointOfSale',
    [string] $SqlExpressSetupPath = '',
    [switch] $PreferLocalDb,
    [switch] $WriteDeploymentOverride
)

$ErrorActionPreference = 'Stop'

function Test-SqlConnection([string] $connectionString) {
    try {
        $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
        $conn.Open()
        $conn.Close()
        return $true
    } catch {
        return $false
    }
}

function Get-MasterConnectionString([string] $server) {
    return "Server=$server;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=8;"
}

function Ensure-Database([string] $server, [string] $dbName) {
    $master = Get-MasterConnectionString $server
    $conn = New-Object System.Data.SqlClient.SqlConnection $master
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @"
IF DB_ID(N'$dbName') IS NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE DATABASE [$dbName];';
    EXEC(@sql);
END
"@
        [void]$cmd.ExecuteNonQuery()
        Write-Host "Database '$dbName' is ready on $server."
    } finally {
        $conn.Close()
    }
}

$expressServer = ".\$InstanceName"
$localDbServer = '(localdb)\MSSQLLocalDB'
$selectedServer = $null

Write-Host "=== Albert Retail Terminal — SQL Express / LocalDB bootstrap ==="

if (-not $PreferLocalDb) {
    if (Test-SqlConnection (Get-MasterConnectionString $expressServer)) {
        $selectedServer = $expressServer
        Write-Host "Detected SQL Server Express: $expressServer"
    }
}

if (-not $selectedServer) {
    if (Test-SqlConnection (Get-MasterConnectionString $localDbServer)) {
        $selectedServer = $localDbServer
        Write-Host "Detected SQL Server LocalDB: $localDbServer"
    }
}

if (-not $selectedServer -and $SqlExpressSetupPath -and (Test-Path $SqlExpressSetupPath)) {
    Write-Host "Launching SQL Express silent install from $SqlExpressSetupPath ..."
    $args = @(
        '/QUIET',
        'IACCEPTSQLSERVERLICENSETERMS=1',
        'ACTION=Install',
        'FEATURES=SQLEngine',
        "INSTANCENAME=$InstanceName",
        'SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE"',
        'SQLSYSADMINACCOUNTS="BUILTIN\Administrators"',
        'TCPENABLED=1',
        'NPENABLED=0'
    )
    $proc = Start-Process -FilePath $SqlExpressSetupPath -ArgumentList $args -Wait -PassThru
    if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 3010) {
        throw "SQL Express setup failed with exit code $($proc.ExitCode)."
    }
    Start-Sleep -Seconds 5
    if (Test-SqlConnection (Get-MasterConnectionString $expressServer)) {
        $selectedServer = $expressServer
    }
}

if (-not $selectedServer) {
    throw @"
No usable SQL engine found.
Install Microsoft SQL Server Express (instance $InstanceName) or SQL Server LocalDB, then re-run this script.
"@
}

Ensure-Database -server $selectedServer -dbName $DatabaseName

if ($WriteDeploymentOverride) {
    $programData = Join-Path $env:ProgramData 'AlbertRetailTerminal'
    New-Item -ItemType Directory -Force -Path $programData | Out-Null
    $overridePath = Join-Path $programData 'appsettings.Deployment.json'
    $cs = "Server=$selectedServer;Database=$DatabaseName;Trusted_Connection=True;TrustServerCertificate=True;"
    $json = @{
        ConnectionStrings = @{ PosDatabase = $cs }
        DatabaseBootstrap = @{ RequiredInstanceHint = $selectedServer }
    } | ConvertTo-Json -Depth 5
    Set-Content -Path $overridePath -Value $json -Encoding UTF8
    Write-Host "Wrote deployment override: $overridePath"
}

Write-Host "SQL bootstrap complete. Server=$selectedServer Database=$DatabaseName"
exit 0
