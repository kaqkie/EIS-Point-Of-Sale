-- Align statutory VAT to 17.5% and requeue failed sales.
UPDATE dbo.Configurations
SET ConfigJson = N'17.5', UpdatedAt = GETUTCDATE()
WHERE ConfigKey = N'Fiscal.StandardVatRatePercent';

UPDATE dbo.Configurations
SET ConfigJson = REPLACE(REPLACE(ConfigJson, '"rate":16.500', '"rate":17.500'), '"rate":16.5', '"rate":17.5')
WHERE ConfigKey = 'mra.configuration.global'
  AND (ConfigJson LIKE '%"rate":16.500%' OR ConfigJson LIKE '%"rate":16.5%');

UPDATE dbo.OfflineInvoiceQueue
SET Status = 'PENDING',
    RetryCount = 0,
    NextRetryTime = NULL,
    ErrorMessage = NULL
WHERE Id IN (3009, 3010);

SELECT
    (SELECT ConfigJson FROM dbo.Configurations WHERE ConfigKey = N'Fiscal.StandardVatRatePercent') AS FiscalVat,
    (SELECT JSON_VALUE(ConfigJson, '$.taxrates[0].rate') FROM dbo.Configurations WHERE ConfigKey = 'mra.configuration.global') AS RateA;

SELECT Id, Status FROM dbo.OfflineInvoiceQueue WHERE Id IN (3009, 3010);
