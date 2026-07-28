using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Onboarding;
using PointOfSale.Mra.Domain.Enums;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Persistence;
using PointOfSale.Mra.Services.Configuration;

namespace PointOfSale.Mra.Services.Onboarding;

public sealed class TerminalOnboardingService
{
    private readonly OnboardingApiService _onboardingApi;
    private readonly ConfigurationApiService _configurationApi;
    private readonly ITerminalStore _terminalStore;
    private readonly MraApiOptions _options;

    public TerminalOnboardingService(
        OnboardingApiService onboardingApi,
        ConfigurationApiService configurationApi,
        ITerminalStore terminalStore,
        Microsoft.Extensions.Options.IOptions<MraApiOptions> options)
    {
        _onboardingApi = onboardingApi;
        _configurationApi = configurationApi;
        _terminalStore = terminalStore;
        _options = options.Value;
    }

    public async Task<TerminalOnboardingResult> ActivateAndConfirmAsync(
        string terminalActivationCode,
        PlatformEnvironmentDto platform,
        CancellationToken cancellationToken = default)
    {
        var activateRequest = new ActivateTerminalRequest
        {
            TerminalActivationCode = terminalActivationCode.Trim(),
            Environment = new TerminalEnvironmentDto
            {
                Platform = platform,
                Pos = new PosEnvironmentDto
                {
                    ProductId = _options.ProductId,
                    ProductVersion = _options.ProductVersion
                }
            }
        };

        var activateResponse = await _onboardingApi
            .ActivateTerminalAsync(activateRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!activateResponse.IsSuccess || activateResponse.Data?.ActivatedTerminal is null)
        {
            return TerminalOnboardingResult.Failed(
                "Terminal activation failed.",
                activateResponse.Remark,
                activateResponse.Errors);
        }

        var activated = activateResponse.Data.ActivatedTerminal;
        var credentials = activated.TerminalCredentials
            ?? throw new InvalidOperationException("Activation response missing terminal credentials.");

        if (string.IsNullOrWhiteSpace(activated.TerminalId) ||
            string.IsNullOrWhiteSpace(credentials.SecretKey) ||
            string.IsNullOrWhiteSpace(credentials.JwtToken))
        {
            throw new InvalidOperationException("Activation response missing terminalId, secretKey, or jwtToken.");
        }

        await _terminalStore.SaveActivationPendingConfirmationAsync(
            new TerminalActivationPersistModel
            {
                TerminalId = activated.TerminalId,
                TerminalActivationCode = terminalActivationCode.Trim(),
                ActivationDateUtc = activated.ActivationDate?.UtcDateTime ?? DateTime.UtcNow,
                JwtToken = credentials.JwtToken,
                SecretKey = credentials.SecretKey,
                ProductId = _options.ProductId,
                ProductVersion = _options.ProductVersion,
                Platform = platform,
                Configuration = activateResponse.Data.Configuration
            },
            cancellationToken).ConfigureAwait(false);

        var confirmResponse = await _onboardingApi
            .ConfirmTerminalActivatedAsync(
                new TerminalActivatedConfirmationRequest { TerminalId = activated.TerminalId },
                terminalActivationCode.Trim(),
                credentials.SecretKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (!confirmResponse.IsSuccess)
        {
            return TerminalOnboardingResult.Failed(
                "Terminal activated locally but MRA confirmation failed. Retry confirmation before selling.",
                confirmResponse.Remark,
                confirmResponse.Errors);
        }

        await _terminalStore.MarkTerminalActivatedAsync(activated.TerminalId, cancellationToken)
            .ConfigureAwait(false);

        return TerminalOnboardingResult.Succeeded(activated.TerminalId, activateResponse.Remark);
    }

    public async Task<ConfigurationSyncResult> SyncLatestConfigurationAsync(
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        var session = await _terminalStore.GetTerminalSessionAsync(terminalId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Terminal '{terminalId}' was not found.");

        if (string.IsNullOrWhiteSpace(session.JwtToken))
        {
            throw new InvalidOperationException("Terminal JWT is missing. Complete onboarding first.");
        }

        try
        {
            var response = await _configurationApi
                .GetLatestConfigurationAsync(session.JwtToken, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Data is null)
            {
                return BuildLocalCredentialFallback(
                    terminalId,
                    session,
                    response.Remark ?? "get-latest-configs returned a non-success EIS payload.",
                    response.Errors);
            }

            var bundle = new EisConfigurationBundleDto
            {
                GlobalConfiguration = response.Data.GlobalConfiguration,
                TerminalConfiguration = response.Data.TerminalConfiguration,
                TaxpayerConfiguration = response.Data.TaxpayerConfiguration
            };

            await _terminalStore.SaveConfigurationBundleAsync(
                terminalId,
                ConfigurationSource.GetLatestConfigs,
                bundle,
                cancellationToken).ConfigureAwait(false);

            return ConfigurationSyncResult.Succeeded(bundle);
        }
        catch (MraApiException ex) when (ex.HttpStatusCode >= 500 || ex.HttpStatusCode == 0)
        {
            // Sandbox HTTP 500 / transport failures must not crash sync — reuse stored credentials.
            return BuildLocalCredentialFallback(terminalId, session, ex.Message, errors: null);
        }
        catch (MraApiException ex)
        {
            return BuildLocalCredentialFallback(terminalId, session, ex.Message, errors: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            return BuildLocalCredentialFallback(terminalId, session, ex.Message, errors: null);
        }
    }

    /// <summary>
    /// Continues offline when live get-latest-configs fails (e.g. sandbox HTTP 500) by reusing the
    /// activated terminal session credentials already persisted in the local store.
    /// </summary>
    private static ConfigurationSyncResult BuildLocalCredentialFallback(
        string terminalId,
        TerminalSession session,
        string? remark,
        IReadOnlyList<Contracts.Common.EisApiError>? errors)
    {
        if (string.IsNullOrWhiteSpace(session.JwtToken))
        {
            return ConfigurationSyncResult.Failed(remark, errors);
        }

        var fallbackRemark =
            $"Live get-latest-configs unavailable. Using local activation credentials for {terminalId} " +
            $"(config versions g/t/tp={session.GlobalConfigVersion}/{session.TerminalConfigVersion}/{session.TaxpayerConfigVersion}). " +
            (string.IsNullOrWhiteSpace(remark) ? string.Empty : remark);

        return ConfigurationSyncResult.SucceededFromLocalFallback(
            new EisConfigurationBundleDto(),
            fallbackRemark);
    }
}

public sealed class TerminalOnboardingResult
{
    public bool Success { get; init; }
    public string? TerminalId { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<Contracts.Common.EisApiError>? Errors { get; init; }

    public static TerminalOnboardingResult Succeeded(string terminalId, string? remark) =>
        new() { Success = true, TerminalId = terminalId, Remark = remark };

    public static TerminalOnboardingResult Failed(
        string remark,
        string? apiRemark,
        IReadOnlyList<Contracts.Common.EisApiError>? errors) =>
        new()
        {
            Success = false,
            Remark = string.IsNullOrWhiteSpace(apiRemark) ? remark : $"{remark} {apiRemark}",
            Errors = errors
        };
}

public sealed class ConfigurationSyncResult
{
    public bool Success { get; init; }
    public bool UsedLocalFallback { get; init; }
    public bool IsUsable => Success || UsedLocalFallback;
    public EisConfigurationBundleDto? Configuration { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<Contracts.Common.EisApiError>? Errors { get; init; }

    public static ConfigurationSyncResult Succeeded(EisConfigurationBundleDto configuration) =>
        new() { Success = true, Configuration = configuration };

    public static ConfigurationSyncResult SucceededFromLocalFallback(
        EisConfigurationBundleDto configuration,
        string? remark) =>
        new()
        {
            Success = false,
            UsedLocalFallback = true,
            Configuration = configuration,
            Remark = remark
        };

    public static ConfigurationSyncResult Failed(
        string? remark,
        IReadOnlyList<Contracts.Common.EisApiError>? errors) =>
        new() { Success = false, Remark = remark, Errors = errors };
}
