// Sync local DB after live EIS confirmation already succeeded (Bearer + HMAC).
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PointOfSale.Infrastructure.Security;

var cs = "Server=.\\SQLEXPRESS;Database=PointOfSale;Trusted_Connection=True;TrustServerCertificate=True;";
await using var conn = new SqlConnection(cs);
await conn.OpenAsync();

const string terminalId = "35e9e5c8-1168-4196-9771-c0381b08bcdc";
var protector = new DpapiSecretProtector();

var pendingJson = await conn.QuerySingleOrDefaultAsync<string>(
    "SELECT ConfigJson FROM dbo.Configurations WHERE ConfigKey='mra.onboarding.pendingSecretKey';");
if (string.IsNullOrWhiteSpace(pendingJson))
{
    Console.WriteLine("No pending secret row.");
    return 1;
}

if (pendingJson.Contains("\"cleared\"", StringComparison.Ordinal))
{
    Console.WriteLine("Pending secret already cleared — ensuring Activated state.");
}
else
{
    using var doc = JsonDocument.Parse(pendingJson);
    var protectedValue = doc.RootElement.GetProperty("ProtectedValue").GetString()
        ?? throw new InvalidOperationException("No ProtectedValue");
    var plain = protector.Unprotect(protectedValue);
    var terminalProtected = protector.Protect(plain);

    await conn.ExecuteAsync(
        """
        UPDATE dbo.Terminals
        SET ActivationState = 'Activated',
            SecretKey = @SecretKey,
            LastSyncedAt = GETUTCDATE()
        WHERE TerminalId = @TerminalId;
        """,
        new { TerminalId = terminalId, SecretKey = terminalProtected });

    await conn.ExecuteAsync(
        """
        MERGE dbo.Configurations AS target
        USING (SELECT 'mra.onboarding.pendingSecretKey' AS ConfigKey) AS source
        ON target.ConfigKey = source.ConfigKey
        WHEN MATCHED THEN UPDATE SET ConfigJson = @Json, UpdatedAt = GETUTCDATE()
        WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (source.ConfigKey, @Json, GETUTCDATE());
        """,
        new { Json = """{"cleared":true}""" });
}

await conn.ExecuteAsync(
    """
    MERGE dbo.Configurations AS target
    USING (SELECT 'Mra.Onboarding.Completed' AS ConfigKey) AS source
    ON target.ConfigKey = source.ConfigKey
    WHEN MATCHED THEN UPDATE SET ConfigJson = 'true', UpdatedAt = GETUTCDATE()
    WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (source.ConfigKey, 'true', GETUTCDATE());
    """);

await conn.ExecuteAsync(
    "DELETE FROM dbo.Terminals WHERE TerminalId <> @TerminalId AND ActivationState = 'PendingConfirmation';",
    new { TerminalId = terminalId });

var state = await conn.QuerySingleAsync<string>(
    "SELECT ActivationState FROM dbo.Terminals WHERE TerminalId=@TerminalId",
    new { TerminalId = terminalId });
Console.WriteLine($"Local terminal {terminalId} => {state}");
return 0;
