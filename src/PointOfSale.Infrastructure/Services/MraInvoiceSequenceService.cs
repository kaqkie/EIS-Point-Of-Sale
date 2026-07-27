using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Core.Constants;
using PointOfSale.Mra.Billing;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Atomically reserves MRA daily transaction counts. Each call returns a fresh invoice number —
/// never cache the result across sales.
/// </summary>
public interface IMraInvoiceSequenceService
{
    /// <summary>
    /// Reserves the next daily sequence value and builds the official MRA composite invoice number
    /// at the moment of the call (commit time). Thread-safe across the process.
    /// </summary>
    Task<string> ReserveNextInvoiceNumberAsync(
        long taxpayerId,
        int terminalPosition,
        DateTime transactionUtc,
        CancellationToken cancellationToken = default);
}

public sealed class MraInvoiceSequenceService : IMraInvoiceSequenceService
{
    private static readonly SemaphoreSlim ReservationGate = new(1, 1);

    private readonly IServiceScopeFactory _scopeFactory;

    public MraInvoiceSequenceService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> ReserveNextInvoiceNumberAsync(
        long taxpayerId,
        int terminalPosition,
        DateTime transactionUtc,
        CancellationToken cancellationToken = default)
    {
        if (taxpayerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taxpayerId), "Taxpayer ID must be a positive number.");
        }

        if (terminalPosition <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalPosition), "Terminal position must be positive.");
        }

        await ReservationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var configRepo = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();

            var julianDate = MraInvoiceNumberGenerator.ToJulianDate(transactionUtc);
            var sequenceKey = $"{MraConfigurationKeys.DailyInvoiceSequencePrefix}{julianDate}";

            var nextCount = await ReadNextDailyCountAsync(configRepo, sequenceKey, cancellationToken)
                .ConfigureAwait(false);
            await PersistDailyCountAsync(configRepo, sequenceKey, nextCount, cancellationToken)
                .ConfigureAwait(false);

            return MraInvoiceNumberGenerator.Generate(taxpayerId, terminalPosition, transactionUtc, nextCount);
        }
        finally
        {
            ReservationGate.Release();
        }
    }

    private static async Task<long> ReadNextDailyCountAsync(
        IConfigurationRepository configRepo,
        string sequenceKey,
        CancellationToken cancellationToken)
    {
        var json = await configRepo.GetJsonAsync(sequenceKey, cancellationToken).ConfigureAwait(false);
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

    private static Task PersistDailyCountAsync(
        IConfigurationRepository configRepo,
        string sequenceKey,
        long count,
        CancellationToken cancellationToken) =>
        configRepo.UpsertJsonAsync(
            sequenceKey,
            JsonSerializer.Serialize(new { count }, MraJson.SerializerOptions),
            cancellationToken);
}
