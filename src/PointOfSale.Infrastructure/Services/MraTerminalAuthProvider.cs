using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;

namespace PointOfSale.Infrastructure.Services;

public interface IMraTerminalAuthProvider
{
    Task<MraRequestContext> GetJwtContextAsync(CancellationToken cancellationToken = default);
    Task<MraRequestContext> GetSignedContextAsync(CancellationToken cancellationToken = default);
    Task<string> GetActiveTerminalIdAsync(CancellationToken cancellationToken = default);
}

public sealed class MraTerminalAuthProvider : IMraTerminalAuthProvider
{
    private readonly ITerminalRepository _terminalRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProtector _secretProtector;

    public MraTerminalAuthProvider(
        ITerminalRepository terminalRepository,
        IConfigurationRepository configurationRepository,
        ISecretProtector secretProtector)
    {
        _terminalRepository = terminalRepository;
        _configurationRepository = configurationRepository;
        _secretProtector = secretProtector;
    }

    public async Task<string> GetActiveTerminalIdAsync(CancellationToken cancellationToken = default)
    {
        var terminalId = await _terminalRepository.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            throw new InvalidOperationException("No activated terminal is registered for Albert Retail Terminal.");
        }

        return terminalId;
    }

    public async Task<MraRequestContext> GetJwtContextAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException("Terminal JWT is missing. Complete onboarding before stock operations.");
        }

        return new MraRequestContext { JwtToken = jwt, UseBearerAuthorization = true };
    }

    public async Task<MraRequestContext> GetSignedContextAsync(CancellationToken cancellationToken = default)
    {
        var terminalId = await GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        var terminal = await _terminalRepository.GetByIdAsync(terminalId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Terminal '{terminalId}' was not found.");

        if (string.IsNullOrWhiteSpace(terminal.SecretKey))
        {
            throw new InvalidOperationException("Terminal secret key is missing. Confirm terminal activation first.");
        }

        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException("Terminal JWT is missing. Complete onboarding before stock operations.");
        }

        var secretKey = _secretProtector.Unprotect(terminal.SecretKey);
        // Live EIS sandbox requires Bearer for authenticated routes (raw JWT → opaque HTTP 500).
        return new MraRequestContext
        {
            JwtToken = jwt,
            SecretKey = secretKey,
            UseBearerAuthorization = true
        };
    }
}
