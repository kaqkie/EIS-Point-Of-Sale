using PointOfSale.Infrastructure.Services;

namespace PointOfSale.Infrastructure.Testing;

public sealed class SandboxMraAuthProvider : IMraTerminalAuthProvider
{
    public string JwtToken { get; init; } = "Bearer sandbox-jwt";

    public string SecretKey { get; init; } = SandboxIntegrationHarness.DefaultSecretKey;

    public string TerminalId { get; init; } = SandboxIntegrationHarness.DefaultTerminalId;

    public bool UseWrongSignatureForNextRequest { get; set; }

    public Task<string> GetActiveTerminalIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TerminalId);

    public Task<MraRequestContext> GetJwtContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MraRequestContext { JwtToken = JwtToken });

    public Task<MraRequestContext> GetSignedContextAsync(CancellationToken cancellationToken = default)
    {
        var secret = UseWrongSignatureForNextRequest ? "intentionally-wrong-hmac-secret" : SecretKey;
        return Task.FromResult(new MraRequestContext { JwtToken = JwtToken, SecretKey = secret });
    }
}
