using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;
using PointOfSale.Mra.Security;

namespace PointOfSale.Infrastructure.Http;

/// <summary>
/// Outgoing MRA EIS interceptor that attaches <c>x-eis-message-hash</c> (HMAC-SHA512 → Base64)
/// to every request except <c>onboarding/activate-terminal</c>.
/// </summary>
public sealed class MraEisMessageHashHandler : DelegatingHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MraEisMessageHashHandler> _logger;

    public MraEisMessageHashHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<MraEisMessageHashHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MraEisMessageHash.ShouldAttach(request))
        {
            _logger.LogDebug(
                "Skipping {Header} for terminal activation request {Uri}",
                MraEisMessageHash.HeaderName,
                request.RequestUri);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Headers.Contains(MraEisMessageHash.HeaderName))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var secretKey = await ResolveSecretKeyAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            _logger.LogDebug(
                "No terminal secret available — omitting {Header} for {Uri}",
                MraEisMessageHash.HeaderName,
                request.RequestUri);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var plainText = await ResolvePayloadPlainTextAsync(request, cancellationToken).ConfigureAwait(false);
        var hash = MraEisMessageHash.TryAttach(request, plainText, secretKey);
        if (hash is not null)
        {
            _logger.LogDebug(
                "Attached {Header} for {Method} {Uri}",
                MraEisMessageHash.HeaderName,
                request.Method,
                request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveSecretKeyAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(MraEisMessageHash.SecretKeyOptionKey, out var fromOptions)
            && !string.IsNullOrWhiteSpace(fromOptions))
        {
            return fromOptions;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var terminals = scope.ServiceProvider.GetService<ITerminalRepository>();
        var config = scope.ServiceProvider.GetService<IConfigurationRepository>();
        var protector = scope.ServiceProvider.GetService<ISecretProtector>();
        if (terminals is null || config is null || protector is null)
        {
            return null;
        }

        // Prefer activated terminal secret; fall back to pending activation secret (confirmation window).
        var terminalId = await terminals.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(terminalId))
        {
            var terminal = await terminals.GetByIdAsync(terminalId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(terminal?.SecretKey))
            {
                try
                {
                    return protector.Unprotect(terminal.SecretKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unprotect terminal secret for {Header}.", MraEisMessageHash.HeaderName);
                }
            }
        }

        var pending = await config
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.PendingSecretKey, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(pending) ? null : pending;
    }

    private static async Task<string> ResolvePayloadPlainTextAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(MraEisMessageHash.PlainTextOptionKey, out var overridePlainText)
            && overridePlainText is not null)
        {
            return overridePlainText;
        }

        if (request.Content is null)
        {
            return string.Empty;
        }

        // Parameterless LoadIntoBufferAsync — buffer so the body can be hashed and still sent.
        await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
        return await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
