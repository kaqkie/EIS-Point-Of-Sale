-- Fix stale cached standard VAT (A @ 16.4) and requeue tax-validation failures.
UPDATE dbo.Configurations
SET ConfigJson = REPLACE(ConfigJson, '"rate":16.400', '"rate":16.500')
WHERE ConfigKey = 'mra.configuration.global'
  AND ConfigJson LIKE '%"rate":16.400%';

SELECT @@ROWCOUNT AS GlobalConfigRowsUpdated;
SELECT JSON_VALUE(ConfigJson, '$.taxrates[0].rate') AS RateA
FROM dbo.Configurations
WHERE ConfigKey = 'mra.configuration.global';

UPDATE dbo.OfflineInvoiceQueue
SET Status = 'PENDING',
    RetryCount = 0,
    NextRetryTime = NULL,
    ErrorMessage = NULL
WHERE Id IN (3009, 3010);

SELECT Id, Status, RetryCount, ErrorMessage
FROM dbo.OfflineInvoiceQueue
WHERE Id IN (3009, 3010);
