/*
    Repair fiscal identity for already-activated terminal.
    Set @TaxpayerTin to the registered MRA TIN before running.
    Never leave the sandbox placeholder 1234567890 on legal receipts.
*/
SET NOCOUNT ON;

DECLARE @TaxpayerTin NVARCHAR(32) = N''; -- <-- set registered MRA TIN here
DECLARE @SiteId NVARCHAR(128) = N'City Center';
DECLARE @BranchId NVARCHAR(128) = N'Lilongwe';
DECLARE @TradingName NVARCHAR(256) = N'Till 7';
DECLARE @TerminalId NVARCHAR(64) = N'ART-SBX-B61182AD';

IF (LEN(LTRIM(RTRIM(@TaxpayerTin))) = 0 OR @TaxpayerTin = N'1234567890')
BEGIN
    RAISERROR('Set @TaxpayerTin to the registered MRA taxpayer TIN before running this script.', 16, 1);
    RETURN;
END;

DECLARE @TinJson NVARCHAR(MAX) = N'{"tin":"' + STRING_ESCAPE(@TaxpayerTin, 'json') + N'"}';

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

DECLARE @Taxpayer NVARCHAR(MAX) = N'{"versionNo":1,"tin":"' + STRING_ESCAPE(@TaxpayerTin, 'json') + N'","isVATRegistered":true,"taxOfficeCode":"SBX","activatedTaxRateIds":["A"]}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.taxpayer' AS ConfigKey, @Taxpayer AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Terminal NVARCHAR(MAX) = N'{"versionNo":1,"terminalLabel":"' + STRING_ESCAPE(@TerminalId, 'json')
    + N'","isActiveTerminal":true,"tradingName":"' + STRING_ESCAPE(@TradingName, 'json')
    + N'","terminalSite":{"siteId":"' + STRING_ESCAPE(@SiteId, 'json')
    + N'","siteName":"' + STRING_ESCAPE(@SiteId, 'json')
    + N'"},"offlineLimit":{"maxTransactionAgeInHours":72,"maxCummulativeAmount":5000000}}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.terminal' AS ConfigKey, @Terminal AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Global NVARCHAR(MAX) = N'{"id":1,"versionNo":1,"taxrates":[{"id":"A","name":"Standard VAT","chargeMode":"VAT","ordinal":1,"rate":17.5}]}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.global' AS ConfigKey, @Global AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

SELECT ConfigKey, LEFT(ConfigJson, 140) AS Preview
FROM dbo.Configurations
WHERE ConfigKey IN (
    N'deployment.taxpayer.tin',
    N'deployment.siteId',
    N'deployment.branchId',
    N'mra.configuration.taxpayer',
    N'mra.configuration.terminal',
    N'mra.configuration.global')
ORDER BY ConfigKey;
