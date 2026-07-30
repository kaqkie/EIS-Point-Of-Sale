/*

    Align local fiscal identity caches with live MRA get-latest-configs for TIN 20122074.



    LIVE FACTS (do not invent VAT registration locally):

      - tin = 20122074

      - isVATRegistered = false

      - activatedTaxRateIds = CGT,NRT,PAYE,WHT  (NOT VAT A — MRA must activate A for sales)

      - terminalSite.siteId = BL7a9fe868-d512-4198-8b08-497e8f0fc10a

      - global rate A = 16.4 Item (sandbox); statutory Malawi VAT is 17.5 when portal updates



    Sales submit returns "TIN not found" until MRA enables VAT sales for this TIN.

*/

SET NOCOUNT ON;



DECLARE @TaxpayerTin NVARCHAR(32) = N'20122074';

DECLARE @SiteId NVARCHAR(128) = N'BL7a9fe868-d512-4198-8b08-497e8f0fc10a';

DECLARE @SiteName NVARCHAR(128) = N'Luchenza';

DECLARE @BranchId NVARCHAR(128) = N'Luchenza';

DECLARE @TradingName NVARCHAR(256) = N'LUCHENZA MUNICIPALITY COUNCIL';

DECLARE @TerminalLabel NVARCHAR(256) = N'Till 5';

DECLARE @Phone NVARCHAR(64) = N'0988712686';

DECLARE @Email NVARCHAR(128) = N'emilynkhata@yahoo.com';



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



MERGE dbo.Configurations AS t

USING (SELECT N'deployment.merchant.phone' AS ConfigKey, @Phone AS ConfigJson) AS s

ON t.ConfigKey = s.ConfigKey

WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()

WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());



DECLARE @MerchantAddress NVARCHAR(MAX) = N'["Limbe","Blantyre"]';

MERGE dbo.Configurations AS t

USING (SELECT N'deployment.merchant.address' AS ConfigKey, @MerchantAddress AS ConfigJson) AS s

ON t.ConfigKey = s.ConfigKey

WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()

WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());



MERGE dbo.Configurations AS t

USING (SELECT N'deployment.merchant.email' AS ConfigKey, @Email AS ConfigJson) AS s

ON t.ConfigKey = s.ConfigKey

WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()

WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());



-- Mirror live taxpayer: NOT VAT-registered; do not pretend rate A is activated for sales.

DECLARE @Taxpayer NVARCHAR(MAX) = N'{"versionNo":3550,"tin":"' + STRING_ESCAPE(@TaxpayerTin, 'json')

    + N'","isVATRegistered":false,"taxOfficeCode":"GCO","activatedTaxRateIds":["CGT","NRT","PAYE","WHT"]}';

MERGE dbo.Configurations AS t

USING (SELECT N'mra.configuration.taxpayer' AS ConfigKey, @Taxpayer AS ConfigJson) AS s

ON t.ConfigKey = s.ConfigKey

WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()

WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());



DECLARE @Terminal NVARCHAR(MAX) = N'{"versionNo":1,"terminalLabel":"' + STRING_ESCAPE(@TerminalLabel, 'json')

    + N'","isActiveTerminal":true,"tradingName":"' + STRING_ESCAPE(@TradingName, 'json')

    + N'","phoneNumber":"' + STRING_ESCAPE(@Phone, 'json')

    + N'","emailAddress":"' + STRING_ESCAPE(@Email, 'json')

    + N'","terminalSite":{"siteId":"' + STRING_ESCAPE(@SiteId, 'json')

    + N'","siteName":"' + STRING_ESCAPE(@SiteName, 'json')

    + N'"},"offlineLimit":{"maxTransactionAgeInHours":72,"maxCummulativeAmount":5000000}}';

MERGE dbo.Configurations AS t

USING (SELECT N'mra.configuration.terminal' AS ConfigKey, @Terminal AS ConfigJson) AS s

ON t.ConfigKey = s.ConfigKey

WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()

WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());



DECLARE @Global NVARCHAR(MAX) = N'{"id":1,"versionNo":1,"taxrates":['

    + N'{"id":"A","name":"VAT-A","chargeMode":"Item","ordinal":1,"rate":16.4},'

    + N'{"id":"B","name":"VAT-B","chargeMode":"Item","ordinal":2,"rate":0},'

    + N'{"id":"E","name":"Exempt","chargeMode":"Item","ordinal":3,"rate":0}'

    + N']}';

MERGE dbo.Configurations AS t

USING (SELECT N'mra.configuration.global' AS ConfigKey, @Global AS ConfigJson) AS s

ON t.ConfigKey = s.ConfigKey

WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()

WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());



-- Keep quarantined TIN-not-found rows visible; do not auto-clear — MRA must activate VAT A first.

SELECT ConfigKey, LEFT(ConfigJson, 180) AS Preview

FROM dbo.Configurations

WHERE ConfigKey IN (

    N'deployment.taxpayer.tin',

    N'deployment.siteId',

    N'deployment.branchId',

    N'mra.configuration.taxpayer',

    N'mra.configuration.terminal',

    N'mra.configuration.global')

ORDER BY ConfigKey;



SELECT Id, Status, LEFT(ISNULL(ErrorMessage, N''), 120) AS Err

FROM dbo.OfflineInvoiceQueue

WHERE Id IN (3009, 3010);

