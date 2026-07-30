-- Prefer activated EIS site + VAT rate A for retail sales when sandbox caches are wrong.
-- Terminal site GUID comes from mra.configuration.terminal; deployment SITE-* labels must not win.

DECLARE @TerminalSite nvarchar(200) =
    (SELECT JSON_VALUE(ConfigJson, '$.terminalSite.siteId')
     FROM dbo.Configurations
     WHERE ConfigKey = 'mra.configuration.terminal');

IF @TerminalSite IS NOT NULL AND LEN(@TerminalSite) > 0
BEGIN
    UPDATE dbo.Configurations
    SET ConfigJson = '"' + REPLACE(@TerminalSite, '"', '') + '"'
    WHERE ConfigKey = 'deployment.siteId';
END

-- Ensure standard VAT rate A is activated for POS sales (cached PAYE/WHT-only profiles break tax math).
UPDATE dbo.Configurations
SET ConfigJson = JSON_MODIFY(
        JSON_MODIFY(ConfigJson, '$.isVATRegistered', CAST(1 AS bit)),
        '$.activatedTaxRateIds',
        JSON_QUERY('["A","E"]'))
WHERE ConfigKey = 'mra.configuration.taxpayer';

UPDATE dbo.OfflineInvoiceQueue
SET Status = 'PENDING',
    RetryCount = 0,
    NextRetryTime = NULL,
    ErrorMessage = NULL
WHERE Id IN (3009, 3010);

SELECT
    (SELECT JSON_VALUE(ConfigJson, '$.terminalSite.siteId')
     FROM dbo.Configurations WHERE ConfigKey = 'mra.configuration.terminal') AS TerminalSite,
    (SELECT ConfigJson FROM dbo.Configurations WHERE ConfigKey = 'deployment.siteId') AS DeploymentSite,
    (SELECT JSON_VALUE(ConfigJson, '$.isVATRegistered')
     FROM dbo.Configurations WHERE ConfigKey = 'mra.configuration.taxpayer') AS VatReg,
    (SELECT JSON_QUERY(ConfigJson, '$.activatedTaxRateIds')
     FROM dbo.Configurations WHERE ConfigKey = 'mra.configuration.taxpayer') AS Rates;

SELECT Id, Status FROM dbo.OfflineInvoiceQueue WHERE Id IN (3009, 3010);
