using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Domain.Enums;

namespace PointOfSale.Mra.Persistence;

public interface ITerminalStore
{
    Task SaveActivationPendingConfirmationAsync(
        TerminalActivationPersistModel model,
        CancellationToken cancellationToken = default);

    Task MarkTerminalActivatedAsync(string terminalId, CancellationToken cancellationToken = default);

    Task<TerminalSession?> GetTerminalSessionAsync(
        string terminalId,
        CancellationToken cancellationToken = default);

    Task SaveConfigurationBundleAsync(
        string terminalId,
        ConfigurationSource source,
        EisConfigurationBundleDto bundle,
        CancellationToken cancellationToken = default);
}

public sealed class TerminalActivationPersistModel
{
    public required string TerminalId { get; init; }
    public required string TerminalActivationCode { get; init; }
    public DateTime ActivationDateUtc { get; init; }
    public required string JwtToken { get; init; }
    public required string SecretKey { get; init; }
    public required string ProductId { get; init; }
    public required string ProductVersion { get; init; }
    public required Contracts.Onboarding.PlatformEnvironmentDto Platform { get; init; }
    public EisConfigurationBundleDto? Configuration { get; init; }
}

public sealed class TerminalSession
{
    public required string TerminalId { get; init; }
    public string? JwtToken { get; init; }
    public string? SecretKey { get; init; }
    public int GlobalConfigVersion { get; init; }
    public int TerminalConfigVersion { get; init; }
    public int TaxpayerConfigVersion { get; init; }
}
