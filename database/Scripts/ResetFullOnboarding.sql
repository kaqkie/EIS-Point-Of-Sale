/*
    Full onboarding reset — app behaves like a new install for first-run / activation.
    Keeps: products, operators, sales history, offline queue.
    Clears: first-run flag, software license activation, MRA terminal activation,
            deployment identity, and cached MRA configs.
*/
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

-- Remove terminal row entirely (re-created on activate).
DELETE FROM dbo.Terminals;

-- First-run + license + deployment + MRA onboarding / identity caches.
DELETE FROM dbo.Configurations
WHERE ConfigKey IN (
    N'FirstRun.Completed',
    N'FirstRun.MraEnvironment',
    N'Terminal.License.Activated',
    N'Terminal.License.Payload',
    N'deployment.hardware.fingerprintSha256',
    N'deployment.taxpayer.tin',
    N'deployment.siteId',
    N'deployment.provisionedAtUtc',
    N'deployment.packagingChannel',
    N'deployment.terminal.displayName',
    N'deployment.branchId',
    N'deployment.merchant.address',
    N'deployment.merchant.phone',
    N'deployment.merchant.email',
    N'mra.auth.jwt',
    N'mra.onboarding.terminalActivationCode',
    N'mra.onboarding.pendingSecretKey',
    N'Mra.Onboarding.Completed',
    N'pos.terminal.activeId',
    N'mra.utilities.terminalBlocking.state',
    N'mra.utilities.initialInventoryUpload.state',
    N'mra.configuration.global',
    N'mra.configuration.terminal',
    N'mra.configuration.taxpayer',
    N'mra.eis.fiscalLockoutActive',
    N'mra.eis.runtimeEnvironment'
)
OR ConfigKey LIKE N'mra.utilities.terminalSiteProducts.%'
OR ConfigKey LIKE N'mra.sales.invoiceSequence.%'
OR ConfigKey LIKE N'mra.utilities.vat5.balance.%';

IF OBJECT_ID(N'dbo.TerminalLicenseActivation', N'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.TerminalLicenseActivation;
END;

-- Ensure wizard flags remain available.
MERGE dbo.Configurations AS t
USING (SELECT N'FirstRun.SetupWizardAvailable' AS ConfigKey, N'true' AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

MERGE dbo.Configurations AS t
USING (SELECT N'Terminal.License.RequireActivation' AS ConfigKey, N'true' AS ConfigJson) AS s
ON t.ConfigKey = s.ConfigKey
WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());

SELECT N'Terminals' AS Area, CAST(COUNT(*) AS NVARCHAR(32)) AS Remaining
FROM dbo.Terminals
UNION ALL
SELECT N'FirstRun/License/Deployment/MRA configs', CAST(COUNT(*) AS NVARCHAR(32))
FROM dbo.Configurations
WHERE ConfigKey IN (
    N'FirstRun.Completed', N'Terminal.License.Activated', N'deployment.branchId',
    N'Mra.Onboarding.Completed', N'mra.auth.jwt')
UNION ALL
SELECT N'SetupWizardAvailable', ConfigJson
FROM dbo.Configurations WHERE ConfigKey = N'FirstRun.SetupWizardAvailable';
