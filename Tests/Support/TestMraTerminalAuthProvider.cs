using PointOfSale.Infrastructure.Services;

namespace PointOfSale.Tests.Support;

public sealed class TestMraTerminalAuthProvider : IMraTerminalAuthProvider
{
    public string JwtToken { get; init; } = "Bearer test-jwt-token";

    public string SecretKey { get; init; } = "ART-Integration-Test-Secret-Key";

    public string TerminalId { get; init; } = "TERM-TEST-001";

    public Task<string> GetActiveTerminalIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TerminalId);

    public Task<MraRequestContext> GetJwtContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MraRequestContext { JwtToken = JwtToken });

    public Task<MraRequestContext> GetSignedContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MraRequestContext { JwtToken = JwtToken, SecretKey = SecretKey });
}
