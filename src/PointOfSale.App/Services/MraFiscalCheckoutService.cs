using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.App.Services;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface IMraFiscalCheckoutService
{
    /// <summary>
    /// Refreshes live MRA configs (when activated), then reserves the next compliant invoice number.
    /// </summary>
    Task<(PosRuntimeContext Context, string InvoiceNumber)> PrepareSaleAsync(
        DateTime transactionUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pre-checkout MRA preparation: <c>POST get-latest-configs</c> identity sync + official invoice numbering.
/// </summary>
public sealed class MraFiscalCheckoutService : IMraFiscalCheckoutService
{
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly TerminalOnboardingService _terminalOnboardingService;
    private readonly ITerminalRepository _terminalRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<MraFiscalCheckoutService> _logger;
    private readonly SemaphoreSlim _sequenceGate = new(1, 1);

    public MraFiscalCheckoutService(
        IPosConfigurationService posConfigurationService,
        TerminalOnboardingService terminalOnboardingService,
        ITerminalRepository terminalRepository,
        IConfigurationRepository configurationRepository,
        ILogger<MraFiscalCheckoutService> logger)
    {
        _posConfigurationService = posConfigurationService;
        _terminalOnboardingService = terminalOnboardingService;
        _terminalRepository = terminalRepository;
        _configurationRepository = configurationRepository;
        _logger = logger;
    }

    public async Task<(PosRuntimeContext Context, string InvoiceNumber)> PrepareSaleAsync(
        DateTime transactionUtc,
        CancellationToken cancellationToken = default)
    {
        await SyncLatestConfigsIfActivatedAsync(cancellationToken).ConfigureAwait(false);
        var context = await _posConfigurationService.GetRuntimeContextAsync(cancellationToken).ConfigureAwait(false);
        var invoiceNumber = await ReserveInvoiceNumberAsync(context, transactionUtc, cancellationToken)
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

    private async Task<string> ReserveInvoiceNumberAsync(
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
        var julianDate = MraInvoiceNumberGenerator.ToJulianDate(transactionUtc);
        var sequenceKey = $"{MraConfigurationKeys.DailyInvoiceSequencePrefix}{julianDate}";

        await _sequenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nextCount = await ReadNextDailyCountAsync(sequenceKey, cancellationToken).ConfigureAwait(false);
            await PersistDailyCountAsync(sequenceKey, nextCount, cancellationToken).ConfigureAwait(false);
            return MraInvoiceNumberGenerator.Generate(taxpayerId, terminalPosition, transactionUtc, nextCount);
        }
        finally
        {
            _sequenceGate.Release();
        }
    }

    private async Task<long> ReadNextDailyCountAsync(string sequenceKey, CancellationToken cancellationToken)
    {
        var json = await _configurationRepository.GetJsonAsync(sequenceKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return 1;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("count", out var countElement) &&
                countElement.TryGetInt64(out var count) &&
                count >= 0)
            {
                return count + 1;
            }
        }
        catch (JsonException)
        {
            // Reset corrupt counter.
        }

        return 1;
    }

    private Task PersistDailyCountAsync(string sequenceKey, long count, CancellationToken cancellationToken) =>
        _configurationRepository.UpsertJsonAsync(
            sequenceKey,
            JsonSerializer.Serialize(new { count }, MraJson.SerializerOptions),
            cancellationToken);
}
