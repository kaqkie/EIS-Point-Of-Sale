$connectionString = "Server=.\SQLEXPRESS;Database=PointOfSale;Trusted_Connection=True;TrustServerCertificate=True;"
$sqlPath = Join-Path $PSScriptRoot "seed_demo_local_inventory.sql"
$sql = Get-Content -Raw -Path $sqlPath

$connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    [void]$command.ExecuteNonQuery()

    $countCommand = $connection.CreateCommand()
    $countCommand.CommandText = "SELECT COUNT(*) FROM dbo.LocalInventory"
    $count = $countCommand.ExecuteScalar()
    Write-Output "InventoryCount=$count"
}
finally {
    $connection.Close()
}
