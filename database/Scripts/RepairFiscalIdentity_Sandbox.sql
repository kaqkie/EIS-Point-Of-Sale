/*
    Repair fiscal identity to match live EIS sample receipt:
    LUCHENZA MUNICIPALITY COUNCIL / TIN 20122074 / VAT A @ 16.5% / not VAT registered.
*/
SET NOCOUNT ON;

DECLARE @TaxpayerTin NVARCHAR(32) = N'20122074';
DECLARE @SiteId NVARCHAR(128) = N'Luchenza';
DECLARE @BranchId NVARCHAR(128) = N'Lilongwe';
DECLARE @TradingName NVARCHAR(256) = N'LUCHENZA MUNICIPALITY COUNCIL';
DECLARE @TerminalDisplayName NVARCHAR(256) = N'Till 7';
DECLARE @TerminalId NVARCHAR(64) = N'ART-SBX-B61182AD';
DECLARE @TerminalLabel NVARCHAR(256) = N'GOVERNMENT COMPLIANCE UNIT';
DECLARE @Phone NVARCHAR(64) = N'0988712686';
DECLARE @Email NVARCHAR(128) = N'emilynkhata@yahoo.com';

DECLARE @TinJson NVARCHAR(MAX) = N'{"tin":"' + STRING_ESCAPE(@TaxpayerTin, 'json') + N'"}';

UPDATE dbo.Terminals
SET BranchCode = @BranchId
WHERE TerminalId = @TerminalId;

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.terminal.displayName' AS ConfigKey, @TerminalDisplayName AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.taxpayer.tin' AS ConfigKey, @TinJson AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.siteId' AS ConfigKey, @SiteId AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.branchId' AS ConfigKey, @BranchId AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.merchant.address' AS ConfigKey, N'["Luchenza"]' AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.merchant.phone' AS ConfigKey, @Phone AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.merchant.email' AS ConfigKey, @Email AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Taxpayer NVARCHAR(MAX) = N'{"versionNo":1,"tin":"' + STRING_ESCAPE(@TaxpayerTin, 'json')
    + N'","isVATRegistered":false,"taxOfficeCode":"SBX","activatedTaxRateIds":["A"]}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.taxpayer' AS ConfigKey, @Taxpayer AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Terminal NVARCHAR(MAX) = N'{"versionNo":1,"terminalLabel":"' + STRING_ESCAPE(@TerminalLabel, 'json')
    + N'","isActiveTerminal":true,"tradingName":"' + STRING_ESCAPE(@TradingName, 'json')
    + N'","terminalSite":{"siteId":"' + STRING_ESCAPE(@SiteId, 'json')
    + N'","siteName":"' + STRING_ESCAPE(@SiteId, 'json')
    + N'"},"offlineLimit":{"maxTransactionAgeInHours":72,"maxCummulativeAmount":5000000}}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.terminal' AS ConfigKey, @Terminal AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Global NVARCHAR(MAX) = N'{"id":1,"versionNo":1,"taxrates":[{"id":"A","name":"Standard VAT","chargeMode":"VAT","ordinal":1,"rate":16.5}]}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.global' AS ConfigKey, @Global AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

SELECT ConfigKey, LEFT(ConfigJson, 160) AS Preview
FROM dbo.Configurations
WHERE ConfigKey IN (
    N'deployment.taxpayer.tin',
    N'deployment.siteId',
    N'deployment.branchId',
    N'mra.configuration.taxpayer',
    N'mra.configuration.terminal',
    N'mra.configuration.global')
ORDER BY ConfigKey;
