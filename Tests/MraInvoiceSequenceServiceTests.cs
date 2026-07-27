using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Testing;
using PointOfSale.Mra.Billing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraInvoiceSequenceServiceTests
{
    [Fact]
    public async Task ReserveNextInvoiceNumberAsync_IncrementsDailySequencePerCall()
    {
        var configRepo = new SandboxConfigurationRepository();
        var service = CreateService(configRepo);
        var transactionUtc = new DateTime(2026, 7, 27, 13, 23, 0, DateTimeKind.Utc);

        var first = await service.ReserveNextInvoiceNumberAsync(20162939, 1, transactionUtc);
        var second = await service.ReserveNextInvoiceNumberAsync(20162939, 1, transactionUtc);

        Assert.NotEqual(first, second);
        Assert.Equal(
            MraInvoiceNumberGenerator.Generate(20162939, 1, transactionUtc, 1),
            first);
        Assert.Equal(
            MraInvoiceNumberGenerator.Generate(20162939, 1, transactionUtc, 2),
            second);
    }

    [Fact]
    public async Task ReserveNextInvoiceNumberAsync_ConcurrentCallsProduceUniqueNumbers()
    {
        var configRepo = new SandboxConfigurationRepository();
        var service = CreateService(configRepo);
        var transactionUtc = DateTime.UtcNow;

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => service.ReserveNextInvoiceNumberAsync(20162939, 1, transactionUtc))
            .ToArray();

        var numbers = await Task.WhenAll(tasks);

        Assert.Equal(20, numbers.Distinct(StringComparer.Ordinal).Count());
    }

    private static MraInvoiceSequenceService CreateService(SandboxConfigurationRepository configRepo)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationRepository>(configRepo);
        services.AddSingleton<IMraInvoiceSequenceService, MraInvoiceSequenceService>();
        return (MraInvoiceSequenceService)services.BuildServiceProvider()
            .GetRequiredService<IMraInvoiceSequenceService>();
    }
}
