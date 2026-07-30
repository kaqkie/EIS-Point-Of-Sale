-- Mark taxpayer VAT-registered (merchant charges 17.5%) and stabilize queue rows.
UPDATE dbo.Configurations
SET ConfigJson = JSON_MODIFY(ConfigJson, '$.isVATRegistered', CAST(1 AS bit))
WHERE ConfigKey = 'mra.configuration.taxpayer';

-- Leave quarantine rows quarantined (do not auto-release validation failures).
UPDATE dbo.OfflineInvoiceQueue
SET Status = 'QUARANTINED',
    NextRetryTime = NULL,
    ErrorMessage = COALESCE(
        NULLIF(ErrorMessage, ''),
        N'MRA EIS: Tax breakdown / validation failure. Review Force Sync after MRA TIN enrolment.')
WHERE Id IN (3009, 3010);

-- Clear any stuck SYNCING rows.
UPDATE dbo.OfflineInvoiceQueue
SET Status = 'QUARANTINED',
    ErrorMessage = COALESCE(ErrorMessage, N'Stuck SYNCING recovered after retry storm.')
WHERE Status = 'SYNCING';

SELECT Id, Status, LEFT(ISNULL(ErrorMessage,''), 120) AS Err
FROM dbo.OfflineInvoiceQueue
WHERE Id IN (3009, 3010) OR Status IN ('SYNCING','PENDING','QUARANTINED')
ORDER BY Id DESC;
