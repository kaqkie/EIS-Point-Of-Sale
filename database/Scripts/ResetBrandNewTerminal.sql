/*

    Brand-new terminal wipe for Albert Retail Terminal (sandbox).



    Clears:

      - Offline invoice queue / fiscal receipts / sales transactions

      - Terminals + software license activation + heartbeats

      - First-run / MRA onboarding / deployment identity / cached configs

      - Invoice sequence counters, taxpayerId, terminal position

      - MRA/compliance/security audit logs, telemetry, sync ledger, shifts

    ALSO REQUIRED after SQL (PowerShell) — otherwise the app restores license/first-run:
      Remove-Item -Recurse -Force HKCU:\Software\AlbertRetail\AlbertRetailTerminal

    Keeps:

      - Operators, LocalInventory / catalog, schema version flags

      - Fiscal.StandardVatRatePercent / Fiscal.VatRuleSource (generic tax defaults)

*/

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;



BEGIN TRAN;



-- 1) Fiscal / offline receipts + sales

IF OBJECT_ID(N'dbo.OfflineInvoiceQueue', N'U') IS NOT NULL

    DELETE FROM dbo.OfflineInvoiceQueue;



IF OBJECT_ID(N'dbo.SalesTransactions', N'U') IS NOT NULL

    DELETE FROM dbo.SalesTransactions;



-- 2) Terminal rows (re-created on activate)

IF OBJECT_ID(N'dbo.Terminals', N'U') IS NOT NULL

    DELETE FROM dbo.Terminals;



IF OBJECT_ID(N'dbo.TerminalLicenseActivation', N'U') IS NOT NULL

    DELETE FROM dbo.TerminalLicenseActivation;



IF OBJECT_ID(N'dbo.TerminalHeartbeat', N'U') IS NOT NULL

    DELETE FROM dbo.TerminalHeartbeat;



-- 3) Shifts / closures / sync / audit / telemetry from the prior terminal

IF OBJECT_ID(N'dbo.CashierShifts', N'U') IS NOT NULL

    DELETE FROM dbo.CashierShifts;



IF OBJECT_ID(N'dbo.FinancialClosures', N'U') IS NOT NULL

    DELETE FROM dbo.FinancialClosures;



IF OBJECT_ID(N'dbo.HeadOfficeSyncOutbox', N'U') IS NOT NULL

    DELETE FROM dbo.HeadOfficeSyncOutbox;



IF OBJECT_ID(N'dbo.MultiTerminalSyncLedger', N'U') IS NOT NULL

    DELETE FROM dbo.MultiTerminalSyncLedger;



IF OBJECT_ID(N'dbo.MultiTerminalSyncCursor', N'U') IS NOT NULL

    DELETE FROM dbo.MultiTerminalSyncCursor;



IF OBJECT_ID(N'dbo.MraApiAuditLog', N'U') IS NOT NULL

    DELETE FROM dbo.MraApiAuditLog;



IF OBJECT_ID(N'dbo.ComplianceAuditLog', N'U') IS NOT NULL

    DELETE FROM dbo.ComplianceAuditLog;



IF OBJECT_ID(N'dbo.SecurityAuditLog', N'U') IS NOT NULL

    DELETE FROM dbo.SecurityAuditLog;



IF OBJECT_ID(N'dbo.DiagnosticTelemetryEvents', N'U') IS NOT NULL

    DELETE FROM dbo.DiagnosticTelemetryEvents;



-- 4) First-run + license + deployment + MRA onboarding / identity caches

DELETE FROM dbo.Configurations

WHERE ConfigKey IN (

    N'FirstRun.Completed',

    N'FirstRun.CompletedUtc',

    N'FirstRun.MraEnvironment',

    N'pos.firstRun.completed',

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

    N'mra.terminal.position',

    N'mra.taxpayer.id',

    N'mra.utilities.terminalBlocking.state',

    N'mra.utilities.initialInventoryUpload.state',

    N'mra.configuration.global',

    N'mra.configuration.terminal',

    N'mra.configuration.taxpayer',

    N'mra.eis.fiscalLockoutActive',

    N'mra.eis.runtimeEnvironment'

)

OR ConfigKey LIKE N'mra.%'

OR ConfigKey LIKE N'Mra.%'

OR ConfigKey LIKE N'deployment.%'

OR ConfigKey LIKE N'pos.terminal.%'

OR ConfigKey LIKE N'mra.utilities.terminalSiteProducts.%'

OR ConfigKey LIKE N'mra.sales.invoiceSequence.%'

OR ConfigKey LIKE N'mra.utilities.vat5.balance.%'

OR ConfigKey LIKE N'%.invoice.sequence%';



-- Keep RequireActivation as a gate (re-assert below); remove any other license leftovers

DELETE FROM dbo.Configurations

WHERE ConfigKey LIKE N'Terminal.License.%'

  AND ConfigKey <> N'Terminal.License.RequireActivation';



-- Wizard / license gates for a fresh first-run

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



COMMIT TRAN;



SELECT N'OfflineInvoiceQueue' AS Area, CAST(COUNT(*) AS NVARCHAR(32)) AS Remaining

FROM dbo.OfflineInvoiceQueue

UNION ALL

SELECT N'Terminals', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.Terminals

UNION ALL

SELECT N'TerminalLicenseActivation', CAST(COUNT(*) AS NVARCHAR(32))

FROM dbo.TerminalLicenseActivation

UNION ALL

SELECT N'MraApiAuditLog', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.MraApiAuditLog

UNION ALL

SELECT N'DiagnosticTelemetryEvents', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.DiagnosticTelemetryEvents

UNION ALL

SELECT N'MultiTerminalSyncLedger', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.MultiTerminalSyncLedger

UNION ALL

SELECT N'CashierShifts', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.CashierShifts

UNION ALL

SELECT N'ComplianceAuditLog', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.ComplianceAuditLog

UNION ALL

SELECT N'SecurityAuditLog', CAST(COUNT(*) AS NVARCHAR(32)) FROM dbo.SecurityAuditLog

UNION ALL

SELECT N'Activation/MRA configs left', CAST(COUNT(*) AS NVARCHAR(32))

FROM dbo.Configurations

WHERE ConfigKey IN (

    N'FirstRun.Completed', N'Terminal.License.Activated', N'deployment.branchId',

    N'Mra.Onboarding.Completed', N'mra.auth.jwt', N'mra.taxpayer.id', N'pos.terminal.activeId')

UNION ALL

SELECT N'SetupWizardAvailable', ConfigJson

FROM dbo.Configurations WHERE ConfigKey = N'FirstRun.SetupWizardAvailable'

UNION ALL

SELECT N'LicenseRequireActivation', ConfigJson

FROM dbo.Configurations WHERE ConfigKey = N'Terminal.License.RequireActivation';

