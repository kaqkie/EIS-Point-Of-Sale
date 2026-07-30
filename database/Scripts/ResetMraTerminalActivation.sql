/*
    Clear local MRA EIS activation so this till can be re-activated from the portal.
    Keeps products, operators, and the offline sales queue.
    Does NOT clear the Albert Retail software license (Terminal.License.*).
*/
SET NOCOUNT ON;

DECLARE @TerminalId NVARCHAR(64) = N'ART-SBX-B61182AD';

-- Drop fake/local Activated state (SecretKey + JWT came from sandbox fallback, not the portal).
UPDATE dbo.Terminals
SET ActivationState = N'NotActivated',
    BranchCode = N'',
    SecretKey = NULL,
    LastSyncedAt = NULL
WHERE TerminalId = @TerminalId;

-- If no row exists yet, insert a NotActivated placeholder for the known terminal id.
IF NOT EXISTS (SELECT 1 FROM dbo.Terminals WHERE TerminalId = @TerminalId)
BEGIN
    INSERT INTO dbo.Terminals (TerminalId, BranchCode, ActivationState, SecretKey, LastSyncedAt)
    VALUES (@TerminalId, N'', N'NotActivated', NULL, NULL);
END;

-- Remove MRA credentials, branch binding, and onboarding flags.
DELETE FROM dbo.Configurations
WHERE ConfigKey IN (
    N'deployment.branchId',
    N'mra.auth.jwt',
    N'mra.onboarding.terminalActivationCode',
    N'mra.onboarding.pendingSecretKey',
    N'Mra.Onboarding.Completed',
    N'pos.terminal.activeId',
    N'mra.utilities.terminalBlocking.state',
    N'mra.utilities.initialInventoryUpload.state',
    -- Cached configs from the fake activation (re-seeded after live activate).
    N'mra.configuration.global',
    N'mra.configuration.terminal',
    N'mra.configuration.taxpayer'
);

-- Clear terminal-site product caches (optional; regenerated after activation).
DELETE FROM dbo.Configurations
WHERE ConfigKey LIKE N'mra.utilities.terminalSiteProducts.%';

SELECT
    t.TerminalId,
    t.BranchCode,
    t.ActivationState,
    CASE WHEN t.SecretKey IS NULL OR t.SecretKey = N'' THEN 0 ELSE 1 END AS HasSecretKey,
    t.LastSyncedAt
FROM dbo.Terminals AS t
WHERE t.TerminalId = @TerminalId;

SELECT ConfigKey
FROM dbo.Configurations
WHERE ConfigKey LIKE N'mra.%'
   OR ConfigKey IN (N'Mra.Onboarding.Completed', N'pos.terminal.activeId')
ORDER BY ConfigKey;
