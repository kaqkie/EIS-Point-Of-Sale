/*
    Repair fiscal identity for already-activated sandbox terminal ART-SBX-B61182AD.
    Seeds TIN + Site so checkout clears "Terminal configuration incomplete".
*/
SET NOCOUNT ON;

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.taxpayer.tin' AS ConfigKey, N'{"tin":"1234567890"}' AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.siteId' AS ConfigKey, N'City Center' AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'deployment.branchId' AS ConfigKey, N'Lilongwe' AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Taxpayer NVARCHAR(MAX) = N'{"versionNo":1,"tin":"1234567890","isVATRegistered":true,"taxOfficeCode":"SBX","activatedTaxRateIds":["A"]}';
MERGE dbo.Configurations AS t
USING (SELECT N'mra.configuration.taxpayer' AS ConfigKey, @Taxpayer AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

DECLARE @Terminal NVARCHAR(MAX) = N'{"versionNo":1,"terminalLabel":"ART-SBX-B61182AD","isActiveTerminal":true,"tradingName":"Till 7","terminalSite":{"siteId":"City Center","siteName":"City Center"},"offlineLimit":{"maxTransactionAgeInHours":72,"maxCummulativeAmount":5000000}}';
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
