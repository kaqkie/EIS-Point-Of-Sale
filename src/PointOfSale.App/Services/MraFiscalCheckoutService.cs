using Microsoft.Extensions.Logging;
using PointOfSale.App.Services;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Billing;

namespace PointOfSale.App.Services;

public interface IMraFiscalCheckoutService
{
    /// <summary>
    /// Refreshes live MRA configs (when activated), then reserves the next compliant invoice number
    /// at commit time. Do not call until the sale is ready to submit.
    /// </summary>
    Task<(PosRuntimeContext Context, string InvoiceNumber)> PrepareSaleAsync(
        DateTime transactionUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pre-checkout MRA preparation: <c>get-latest-configs</c> identity sync + official invoice numbering.
/// Invoice numbers are reserved fresh on each call — never cached between transactions.
/// </summary>
public sealed class MraFiscalCheckoutService : IMraFiscalCheckoutService
{
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly TerminalOnboardingService _terminalOnboardingService;
    private readonly ITerminalRepository _terminalRepository;
    private readonly IMraInvoiceSequenceService _invoiceSequenceService;
    private readonly ILogger<MraFiscalCheckoutService> _logger;

    public MraFiscalCheckoutService(
        IPosConfigurationService posConfigurationService,
        TerminalOnboardingService terminalOnboardingService,
        ITerminalRepository terminalRepository,
        IMraInvoiceSequenceService invoiceSequenceService,
        ILogger<MraFiscalCheckoutService> logger)
    {
        _posConfigurationService = posConfigurationService;
        _terminalOnboardingService = terminalOnboardingService;
        _terminalRepository = terminalRepository;
        _invoiceSequenceService = invoiceSequenceService;
        _logger = logger;
    }

    public async Task<(PosRuntimeContext Context, string InvoiceNumber)> PrepareSaleAsync(
        DateTime transactionUtc,
        CancellationToken cancellationToken = default)
    {
        var sync = await SyncLatestConfigsIfActivatedAsync(cancellationToken).ConfigureAwait(false);
        var context = await _posConfigurationService.GetRuntimeContextAsync(cancellationToken).ConfigureAwait(false);

        if (sync.IsActivated && !sync.Succeeded)
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: get-latest-configs failed for the activated terminal. " +
                "Renew/re-activate the terminal JWT, then retry so sellerTIN, siteId, taxRateId, " +
                "and config versions come from the live configuration sync. " +
                $"Detail: {sync.Remark ?? "opaque EIS error"}");
        }

        // Sandbox/trial may use the developer seed TIN; Production must have a real TIN.
        if (string.IsNullOrWhiteSpace(context.SellerTin))
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: sellerTIN is missing. " +
                "Set TerminalDeployment:TaxpayerTin or complete terminal activation.");
        }

        if (!context.AllowSandboxDeveloperTin
            && (PosConfigurationService.IsPlaceholderTaxpayerTin(context.SellerTin)
                || (MraInvoiceNumberGenerator.TryParseTaxpayerId(context.SellerTin, out var tinCheck)
                    && MraInvoiceNumberGenerator.IsSandboxPlaceholderTaxpayerId(tinCheck))))
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: sellerTIN is missing or still the sandbox placeholder 1234567890. " +
                "Complete terminal activation and confirm get-latest-configs succeeds so invoice numbers " +
                "encode the real Taxpayer ID (not BJlgZ).");
        }

        if (string.IsNullOrWhiteSpace(context.FiscalSiteId)
            || context.FiscalSiteId.Equals("SITE-LOCAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: siteId is missing or local seed only. " +
                "Sync get-latest-configs after terminal activation so invoiceHeader.siteId matches MRA.");
        }

        if (context.Global is null || context.Terminal is null || context.Taxpayer is null)
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: cached global/terminal/taxpayer configuration is incomplete. " +
                "Run get-latest-configs successfully before selling.");
        }

        _logger.LogInformation(
            "Checkout fiscal identity ready. sellerTIN={Tin} siteId={SiteId} taxRateId={TaxRate} versions g/t/tp={Global}/{Terminal}/{Taxpayer} invoiceTinDigits={TinDigits}",
            context.SellerTin,
            context.FiscalSiteId,
            context.StandardVatTaxRateId,
            context.GlobalConfigVersion,
            context.TerminalConfigVersion,
            context.TaxpayerConfigVersion,
            MraInvoiceNumberGenerator.TryParseTaxpayerId(context.SellerTin, out var tinDigits) ? tinDigits : 0);

        var invoiceNumber = await ReserveInvoiceNumberAtCommitAsync(context, transactionUtc, cancellationToken)
            .ConfigureAwait(false);
        return (context, invoiceNumber);
    }

    private async Task<ConfigSyncAttempt> SyncLatestConfigsIfActivatedAsync(CancellationToken cancellationToken)
    {
        var terminalId = await _terminalRepository.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return ConfigSyncAttempt.NotActivated();
        }

        var terminal = await _terminalRepository.GetByIdAsync(terminalId, cancellationToken).ConfigureAwait(false);
        if (terminal is null ||
            !string.Equals(terminal.ActivationState, TerminalActivationStates.Activated, StringComparison.OrdinalIgnoreCase))
        {
            return ConfigSyncAttempt.NotActivated();
        }

        try
        {
            var result = await _terminalOnboardingService.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
            if (result.UsedLocalFallback)
            {
                _logger.LogWarning(
                    "Pre-checkout get-latest-configs unavailable for {TerminalId}; continuing with local activation fallback. {Remark}",
                    terminalId,
                    result.Remark ?? "(null)");
                return ConfigSyncAttempt.Ok();
            }

            if (!result.Success)
            {
                _logger.LogWarning(
                    "Pre-checkout get-latest-configs failed for {TerminalId}: {Remark}",
                    terminalId,
                    result.Remark ?? "(null)");
                return ConfigSyncAttempt.Failed(result.Remark);
            }

            return ConfigSyncAttempt.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-checkout get-latest-configs threw for {TerminalId}.", terminalId);
            return ConfigSyncAttempt.Failed(ex.Message);
        }
    }

    private async Task<string> ReserveInvoiceNumberAtCommitAsync(
        PosRuntimeContext context,
        DateTime transactionUtc,
        CancellationToken cancellationToken)
    {
        if (!MraInvoiceNumberGenerator.TryParseTaxpayerId(context.SellerTin, out _)
            && context.ResolveFiscalTaxpayerId() <= 0)
        {
            throw new InvalidOperationException(
                "Cannot generate MRA invoice number: seller TIN / fiscal taxpayer id is missing. " +
                "Complete terminal activation and configuration sync first.");
        }

        var taxpayerId = context.ResolveFiscalTaxpayerId();
        if (taxpayerId <= 0)
        {
            throw new InvalidOperationException(
                "Cannot generate MRA invoice number: fiscal taxpayer id is missing. " +
                "Re-activate the terminal so MRA taxpayerId is stored.");
        }

        var terminalPosition = context.TerminalPosition > 0 ? context.TerminalPosition : 1;
        var invoiceNumber = await _invoiceSequenceService
            .ReserveNextInvoiceNumberAsync(taxpayerId, terminalPosition, transactionUtc, cancellationToken)
            .ConfigureAwait(false);

        if (!MraInvoiceNumberGenerator.IsMraCompositeInvoiceNumber(invoiceNumber))
        {
            throw new InvalidOperationException(
                $"Generated invoice number '{invoiceNumber}' is not MRA composite " +
                "Base64(TaxpayerID)-Base64(TerminalPosition)-Base64(JulianDate)-Base64(Count).");
        }

        return invoiceNumber;
    }

    private readonly record struct ConfigSyncAttempt(bool IsActivated, bool Succeeded, string? Remark)
    {
        public static ConfigSyncAttempt NotActivated() => new(false, true, null);
        public static ConfigSyncAttempt Ok() => new(true, true, null);
        public static ConfigSyncAttempt Failed(string? remark) => new(true, false, remark);
    }
}
