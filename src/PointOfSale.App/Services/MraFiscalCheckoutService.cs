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
        await SyncLatestConfigsIfActivatedAsync(cancellationToken).ConfigureAwait(false);
        var context = await _posConfigurationService.GetRuntimeContextAsync(cancellationToken).ConfigureAwait(false);

        if (PosConfigurationService.IsPlaceholderTaxpayerTin(context.SellerTin))
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: sellerTIN is still the sandbox placeholder 1234567890. " +
                "Re-activate the terminal (JWT may be expired) and confirm get-latest-configs succeeds " +
                "so sellerTIN/siteId/taxRateId/config versions match activation.");
        }

        if (string.IsNullOrWhiteSpace(context.FiscalSiteId)
            || context.FiscalSiteId.Equals("SITE-LOCAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Cannot submit to MRA: siteId is missing or local seed only. " +
                "Sync get-latest-configs after terminal activation so invoiceHeader.siteId matches MRA.");
        }

        var invoiceNumber = await ReserveInvoiceNumberAtCommitAsync(context, transactionUtc, cancellationToken)
            .ConfigureAwait(false);
        return (context, invoiceNumber);
    }

    private async Task SyncLatestConfigsIfActivatedAsync(CancellationToken cancellationToken)
    {
        var terminalId = await _terminalRepository.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return;
        }

        var terminal = await _terminalRepository.GetByIdAsync(terminalId, cancellationToken).ConfigureAwait(false);
        if (terminal is null ||
            !string.Equals(terminal.ActivationState, TerminalActivationStates.Activated, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var result = await _terminalOnboardingService.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "Pre-checkout get-latest-configs failed for {TerminalId}: {Remark}",
                    terminalId,
                    result.Remark ?? "(null)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-checkout get-latest-configs threw for {TerminalId}; using cached configs.", terminalId);
        }
    }

    private async Task<string> ReserveInvoiceNumberAtCommitAsync(
        PosRuntimeContext context,
        DateTime transactionUtc,
        CancellationToken cancellationToken)
    {
        if (!MraInvoiceNumberGenerator.TryParseTaxpayerId(context.SellerTin, out var taxpayerId))
        {
            throw new InvalidOperationException(
                "Cannot generate MRA invoice number: seller TIN is missing or not numeric. " +
                "Complete terminal activation and configuration sync first.");
        }

        var terminalPosition = context.TerminalPosition > 0 ? context.TerminalPosition : 1;
        return await _invoiceSequenceService
            .ReserveNextInvoiceNumberAsync(taxpayerId, terminalPosition, transactionUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
