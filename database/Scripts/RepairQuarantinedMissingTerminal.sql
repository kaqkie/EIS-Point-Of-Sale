-- Re-queue offline sales that failed only because the till was not activated yet.
-- Safe to re-run. Does not touch SYNCED rows or other quarantine reasons.

UPDATE dbo.OfflineInvoiceQueue
SET Status = N'PENDING',
    RetryCount = 0,
    NextRetryTime = NULL,
    ErrorMessage = NULL
WHERE Status = N'QUARANTINED'
  AND (
        ErrorMessage LIKE N'%No activated terminal%'
     OR ErrorMessage LIKE N'%Offline compliance preparation failed: No activated terminal%'
     OR ErrorMessage LIKE N'%Invalid payload: No activated terminal%'
  );

SELECT Id, Status, RetryCount, LEFT(ErrorMessage, 80) AS Err
FROM dbo.OfflineInvoiceQueue
WHERE Id IN (3009, 3010)
ORDER BY Id;
