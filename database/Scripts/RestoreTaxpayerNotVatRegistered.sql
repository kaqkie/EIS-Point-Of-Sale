-- Restore taxpayer VAT flag; EIS sample for this merchant is not VAT-registered.
UPDATE dbo.Configurations
SET ConfigJson = JSON_MODIFY(ConfigJson, '$.isVATRegistered', CAST(0 AS bit))
WHERE ConfigKey = 'mra.configuration.taxpayer';

SELECT JSON_VALUE(ConfigJson, '$.isVATRegistered') AS VatReg,
       JSON_QUERY(ConfigJson, '$.activatedTaxRateIds') AS Rates
FROM dbo.Configurations
WHERE ConfigKey = 'mra.configuration.taxpayer';
