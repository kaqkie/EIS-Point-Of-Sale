using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Models;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Onboarding;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;
using PointOfSale.Mra.Services;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;

namespace PointOfSale.Infrastructure.Services;

public sealed class TerminalOnboardingService
{
    private readonly MraApiClient _apiClient;
    private readonly ITerminalRepository _terminalRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IMraEisResponseEvaluator _responseEvaluator;
    private readonly MraApiOptions _options;
    private readonly ILogger<TerminalOnboardingService> _logger;

    public TerminalOnboardingService(
        MraApiClient apiClient,
        ITerminalRepository terminalRepository,
        IConfigurationRepository configurationRepository,
        ISecretProtector secretProtector,
        IOptions<MraApiOptions> options,
        ILogger<TerminalOnboardingService> logger,
        IMraEisResponseEvaluator? responseEvaluator = null)
    {
        _apiClient = apiClient;
        _terminalRepository = terminalRepository;
        _configurationRepository = configurationRepository;
        _secretProtector = secretProtector;
        _options = options.Value;
        _logger = logger;
        _responseEvaluator = responseEvaluator
            ?? new MraEisResponseEvaluator(NullLogger<MraEisResponseEvaluator>.Instance);
    }

    public async Task<TerminalActivationResult> ActivateTerminalAsync(
        TerminalActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!MraVendorAccessKeyPolicy.TryResolveForActivateTerminal(_options, out var accessKey, out var accessKeyError))
        {
            _logger.LogError("Production activate-terminal blocked: {Error}", accessKeyError);
            return TerminalActivationResult.Failed(
                accessKeyError,
                errors: null,
                statusCode: MraEisStatusCodes.AuthenticationFailure,
                operatorMessage: accessKeyError);
        }

        var apiRequest = new ActivateTerminalRequest
        {
            TerminalActivationCode = request.TerminalActivationCode.Trim(),
            Environment = new TerminalEnvironmentDto
            {
                Platform = new PlatformEnvironmentDto
                {
                    OsName = request.Platform.OsName,
                    OsVersion = request.Platform.OsVersion,
                    OsBuild = request.Platform.OsBuild,
                    MacAddress = request.Platform.MacAddress
                },
                Pos = new PosEnvironmentDto
                {
                    ProductId = request.Pos.ProductId,
                    ProductVersion = request.Pos.ProductVersion
                }
            }
        };

        EisApiResponse<ActivateTerminalResponseData> response;
        try
        {
            response = await _apiClient
                .PostAsync<ActivateTerminalRequest, ActivateTerminalResponseData>(
                    "onboarding/activate-terminal",
                    apiRequest,
                    context: new MraRequestContext { VendorAccessKey = accessKey },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MraApiException ex)
        {
            var evaluation = _responseEvaluator.EvaluateException(ex);
            _logger.LogWarning(
                ex,
                "activate-terminal HTTP failure. category={Category} action={Action} http={HttpStatus}",
                evaluation.Category,
                evaluation.RecommendedAction,
                ex.HttpStatusCode);
            return TerminalActivationResult.FailedFromEvaluation(evaluation);
        }

        var logical = _responseEvaluator.Evaluate(response);
        if (!logical.IsSuccess
            || response.Data?.ActivatedTerminal?.TerminalCredentials is null)
        {
            _logger.LogWarning(
                "activate-terminal logical failure. statusCode={StatusCode} category={Category} remark={Remark}",
                response.StatusCode,
                logical.Category,
                response.Remark ?? "(null)");
            return TerminalActivationResult.FailedFromEvaluation(logical);
        }

        var activated = response.Data.ActivatedTerminal;
        var credentials = activated.TerminalCredentials!;
        var terminalId = activated.TerminalId;
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return TerminalActivationResult.Failed(
                "MRA activation response did not include terminalId.",
                response.Errors,
                response.StatusCode,
                "MRA activation response was incomplete (missing terminalId). Contact MRA support or retry.");
        }

        if (string.IsNullOrWhiteSpace(credentials.SecretKey) || string.IsNullOrWhiteSpace(credentials.JwtToken))
        {
            return TerminalActivationResult.Failed(
                "MRA activation response missing jwtToken or secretKey.",
                response.Errors,
                response.StatusCode,
                "MRA activation response was incomplete (missing JWT or secret key). Do not proceed — retry activation.");
        }

        await PersistActivationSecretsAsync(
                terminalId,
                request,
                credentials,
                activated.TerminalPosition,
                response.Data.Configuration,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Terminal {TerminalId} activated pending MRA confirmation (productionAccessKey={UsedAccessKey}).",
            terminalId,
            accessKey is not null);
        return TerminalActivationResult.Succeeded(terminalId, response.Remark, response.Data.Configuration);
    }

    /// <summary>
    /// DPAPI-protects JWT + pending secret key and caches optional config bundle from activation.
    /// </summary>
    private async Task PersistActivationSecretsAsync(
        string terminalId,
        TerminalActivationRequest request,
        TerminalCredentialsDto credentials,
        int? terminalPosition,
        EisConfigurationBundleDto? configuration,
        CancellationToken cancellationToken)
    {
        await _terminalRepository.UpsertPendingActivationAsync(
            new Terminal
            {
                TerminalId = terminalId,
                BranchCode = request.BranchCode,
                ActivationState = TerminalActivationStates.PendingConfirmation,
                LastSyncedAt = DateTime.UtcNow
            },
            cancellationToken).ConfigureAwait(false);

        await _configurationRepository.UpsertJsonAsync(
            MraConfigurationKeys.ActiveTerminalId,
            JsonSerializer.Serialize(new { terminalId }, MraJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        await _configurationRepository.UpsertProtectedSecretAsync(
            MraConfigurationKeys.JwtToken,
            credentials.JwtToken!,
            cancellationToken).ConfigureAwait(false);

        await _configurationRepository.UpsertProtectedSecretAsync(
            MraConfigurationKeys.PendingSecretKey,
            credentials.SecretKey!,
            cancellationToken).ConfigureAwait(false);

        await _configurationRepository.UpsertJsonAsync(
            MraConfigurationKeys.TerminalActivationCode,
            JsonSerializer.Serialize(new { code = request.TerminalActivationCode.Trim() }, MraJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        if (terminalPosition is > 0)
        {
            await _configurationRepository.UpsertJsonAsync(
                    MraConfigurationKeys.TerminalPosition,
                    JsonSerializer.Serialize(new { position = terminalPosition.Value }, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration is not null)
        {
            await CacheConfigurationBundleAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<TerminalConfirmationResult> ConfirmTerminalActivationAsync(
        TerminalActivationConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        var pendingSecret = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.PendingSecretKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(pendingSecret))
        {
            throw new InvalidOperationException(
                "Pending secret key not found. Run ActivateTerminalAsync before confirmation.");
        }

        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException(
                "Activation JWT not found. Run ActivateTerminalAsync before confirmation.");
        }

        var body = new TerminalActivatedConfirmationRequest { TerminalId = request.TerminalId.Trim() };
        var tac = await ResolveConfirmationTacAsync(request.TerminalActivationCode, cancellationToken)
            .ConfigureAwait(false);
        var secret = pendingSecret.Trim();
        var jwtNormalized = MraJwtClaims.NormalizeAuthorizationToken(jwt);

        // Live EIS requires Authorization: Bearer {jwt} plus x-signature = HMAC-SHA512(TAC, secret).
        // Raw JWT (no Bearer) returns opaque HTTP 500; missing Authorization returns 401.
        EisApiResponse<bool> response;
        try
        {
            response = await _apiClient
                .PostAsync<TerminalActivatedConfirmationRequest, bool>(
                    "onboarding/terminal-activated-confirmation",
                    body,
                    new MraRequestContext
                    {
                        JwtToken = jwtNormalized,
                        UseBearerAuthorization = true,
                        SecretKey = secret,
                        SignaturePlainText = tac,
                        IsActivationConfirmationSignature = true,
                        AcceptHeader = "text/plain"
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MraApiException ex)
        {
            _logger.LogWarning(
                "terminal-activated-confirmation HTTP {Status}: {Message}",
                ex.HttpStatusCode,
                ex.Message);
            return TerminalConfirmationResult.Failed(ex.Message, errors: null);
        }

        if (!response.IsSuccess || !response.Data)
        {
            _logger.LogWarning(
                "terminal-activated-confirmation failed. statusCode={StatusCode}, remark={Remark}, data={Data}, errors={Errors}",
                response.StatusCode,
                response.Remark ?? "(null)",
                response.Data,
                response.Errors is null
                    ? "(none)"
                    : JsonSerializer.Serialize(response.Errors, MraJson.SerializerOptions));
            return TerminalConfirmationResult.Failed(response.Remark, response.Errors);
        }

        var protectedSecret = _secretProtector.Protect(secret);
        await _terminalRepository.MarkActivatedAsync(request.TerminalId.Trim(), protectedSecret, cancellationToken)
            .ConfigureAwait(false);

        await _configurationRepository.UpsertJsonAsync(
            MraConfigurationKeys.PendingSecretKey,
            JsonSerializer.Serialize(new { cleared = true }, MraJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        // Official sequence: after confirmation, sync global/terminal/taxpayer configs for sales payloads.
        try
        {
            var configs = await GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
            if (configs.UsedLocalFallback)
            {
                _logger.LogWarning(
                    "Post-confirmation get-latest-configs unavailable for {TerminalId}; continuing with local activation. {Remark}",
                    request.TerminalId,
                    configs.Remark);
            }
            else if (!configs.Success)
            {
                _logger.LogWarning(
                    "Post-confirmation get-latest-configs failed for {TerminalId}: {Remark}",
                    request.TerminalId,
                    configs.Remark);
                await TrySeedTaxpayerTinFromJwtAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Post-confirmation get-latest-configs threw for {TerminalId}; falling back to JWT TIN claim.",
                request.TerminalId);
            await TrySeedTaxpayerTinFromJwtAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Terminal {TerminalId} confirmed with MRA.", request.TerminalId);

        return TerminalConfirmationResult.Succeeded(request.TerminalId, response.Remark);
    }

    private async Task<string> ResolveConfirmationTacAsync(
        string requestedTac,
        CancellationToken cancellationToken)
    {
        var requested = requestedTac?.Trim() ?? string.Empty;
        var storedJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalActivationCode, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(storedJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(storedJson);
                if (doc.RootElement.TryGetProperty("code", out var codeProp))
                {
                    var stored = codeProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(stored))
                    {
                        if (!string.Equals(stored, requested, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(requested))
                        {
                            _logger.LogWarning(
                                "Confirmation TAC from UI differs from stored activate TAC; signing with stored TAC.");
                        }

                        return stored;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse stored terminal activation code; using request TAC.");
            }
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new InvalidOperationException(
                "Terminal activation code not found for confirmation. Re-run activate-terminal.");
        }

        return requested;
    }

    public async Task<LatestConfigurationResult> GetLatestConfigsAsync(CancellationToken cancellationToken = default)
    {
        var terminalId = await _terminalRepository.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No activated terminal found in dbo.Terminals.");

        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException("JWT token missing. Complete terminal activation first.");
        }

        try
        {
            var response = await _apiClient
                .GetLatestConfigsAsync<GetLatestConfigurationResponseData>(jwt, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Data is null)
            {
                _logger.LogWarning(
                    "get-latest-configs failed for {TerminalId}. statusCode={StatusCode}, remark={Remark}, errors={Errors}",
                    terminalId,
                    response.StatusCode,
                    response.Remark ?? "(null)",
                    response.Errors is null
                        ? "(none)"
                        : JsonSerializer.Serialize(response.Errors, MraJson.SerializerOptions));

                return await TryFallbackToLocalActivationAsync(
                        terminalId,
                        response.StatusCode,
                        response.Remark ?? "get-latest-configs returned a non-success EIS payload.",
                        response.Errors,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var bundle = new EisConfigurationBundleDto
            {
                GlobalConfiguration = response.Data.GlobalConfiguration,
                TerminalConfiguration = response.Data.TerminalConfiguration,
                TaxpayerConfiguration = response.Data.TaxpayerConfiguration
            };

            await CacheConfigurationBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            await _terminalRepository.UpdateLastSyncedAsync(terminalId, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Cached get-latest-configs for {TerminalId}. sellerTIN={Tin}, siteId={SiteId}, versions g/t/tp={Global}/{Terminal}/{Taxpayer}",
                terminalId,
                bundle.TaxpayerConfiguration?.Tin ?? "(null)",
                bundle.TerminalConfiguration?.TerminalSite?.SiteId ?? "(null)",
                bundle.GlobalConfiguration?.VersionNo ?? 0,
                bundle.TerminalConfiguration?.VersionNo ?? 0,
                bundle.TaxpayerConfiguration?.VersionNo ?? 0);

            return LatestConfigurationResult.Succeeded(bundle, response.Remark);
        }
        catch (MraApiException ex) when (ex.HttpStatusCode >= 500 || ex.HttpStatusCode == 0)
        {
            // Sandbox often returns opaque HTTP 500 (or transport failure) on get-latest-configs.
            // Do not fail startup / provisioning — fall back to dbo.Terminals activation + cached configs.
            _logger.LogWarning(
                ex,
                "get-latest-configs threw HTTP {StatusCode} for {TerminalId}; attempting local activation fallback.",
                ex.HttpStatusCode,
                terminalId);

            return await TryFallbackToLocalActivationAsync(
                    terminalId,
                    ex.HttpStatusCode,
                    ex.Message,
                    errors: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MraApiException ex)
        {
            _logger.LogWarning(
                ex,
                "get-latest-configs client error HTTP {StatusCode} for {TerminalId}; attempting local activation fallback.",
                ex.HttpStatusCode,
                terminalId);

            return await TryFallbackToLocalActivationAsync(
                    terminalId,
                    ex.HttpStatusCode,
                    ex.Message,
                    errors: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            _logger.LogWarning(
                ex,
                "get-latest-configs unexpected failure for {TerminalId}; attempting local activation fallback.",
                terminalId);

            return await TryFallbackToLocalActivationAsync(
                    terminalId,
                    httpStatusCode: 0,
                    ex.Message,
                    errors: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// When live <c>get-latest-configs</c> is unavailable (e.g. MRA sandbox HTTP 500), continue using
    /// the activated row in <c>dbo.Terminals</c> plus DPAPI-stored JWT/secret and any cached
    /// global/terminal/taxpayer configuration so offline / sandbox test sales can proceed.
    /// </summary>
    private async Task<LatestConfigurationResult> TryFallbackToLocalActivationAsync(
        string terminalId,
        int httpStatusCode,
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors,
        CancellationToken cancellationToken)
    {
        var terminal = await _terminalRepository.GetByIdAsync(terminalId, cancellationToken).ConfigureAwait(false);
        if (terminal is null ||
            !string.Equals(terminal.ActivationState, TerminalActivationStates.Activated, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Local fallback unavailable for {TerminalId}: terminal missing or ActivationState={State}.",
                terminalId,
                terminal?.ActivationState ?? "(null)");
            return LatestConfigurationResult.Failed(remark, errors, httpStatusCode);
        }

        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwt))
        {
            _logger.LogWarning(
                "Local fallback unavailable for {TerminalId}: stored JWT credentials missing.",
                terminalId);
            return LatestConfigurationResult.Failed(
                remark ?? "Activated terminal has no stored JWT for offline fallback.",
                errors,
                httpStatusCode);
        }

        if (string.IsNullOrWhiteSpace(terminal.SecretKey))
        {
            _logger.LogWarning(
                "Local fallback for {TerminalId}: dbo.Terminals.SecretKey is empty; HMAC-signed sales may fail until re-activation.",
                terminalId);
        }

        await TrySeedTaxpayerTinFromJwtAsync(cancellationToken).ConfigureAwait(false);

        var cached = await TryLoadCachedConfigurationBundleAsync(cancellationToken).ConfigureAwait(false);
        var fallbackRemark =
            $"Live get-latest-configs unavailable (HTTP {httpStatusCode}). " +
            $"Using local activation for {terminalId} (ActivationState={terminal.ActivationState}" +
            (terminal.LastSyncedAt is { } synced ? $", LastSyncedAt={synced:O}" : string.Empty) +
            "). " +
            (string.IsNullOrWhiteSpace(remark) ? string.Empty : remark);

        _logger.LogWarning(
            "Using local MRA config fallback for {TerminalId}. ActivationState={State}, hasSecretKey={HasSecret}, hasCachedConfigs={HasCache}. Detail: {Remark}",
            terminalId,
            terminal.ActivationState,
            !string.IsNullOrWhiteSpace(terminal.SecretKey),
            cached is not null,
            fallbackRemark);

        // Prefer previously synced configs; otherwise return an empty bundle so callers can still
        // treat the activated terminal + stored credentials as usable for offline/test flows.
        return LatestConfigurationResult.SucceededFromLocalFallback(
            cached ?? new EisConfigurationBundleDto(),
            fallbackRemark,
            httpStatusCode);
    }

    private async Task<EisConfigurationBundleDto?> TryLoadCachedConfigurationBundleAsync(
        CancellationToken cancellationToken)
    {
        var globalJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.GlobalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        var terminalJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        var taxpayerJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TaxpayerConfiguration, cancellationToken)
            .ConfigureAwait(false);

        var global = TryDeserialize<GlobalConfigurationDto>(globalJson);
        var terminal = TryDeserialize<TerminalConfigurationDto>(terminalJson);
        var taxpayer = TryDeserialize<TaxpayerConfigurationDto>(taxpayerJson);

        if (global is null && terminal is null && taxpayer is null)
        {
            return null;
        }

        return new EisConfigurationBundleDto
        {
            GlobalConfiguration = global,
            TerminalConfiguration = terminal,
            TaxpayerConfiguration = taxpayer
        };
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, MraJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// When get-latest-configs is unavailable, recover sellerTIN from the activation JWT claim
    /// so sales payloads do not keep shipping the sandbox placeholder <c>1234567890</c>.
    /// </summary>
    private async Task TrySeedTaxpayerTinFromJwtAsync(CancellationToken cancellationToken)
    {
        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);
        var tin = MraJwtClaims.TryGetTaxpayerTin(jwt);
        if (string.IsNullOrWhiteSpace(tin))
        {
            return;
        }

        var existingJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TaxpayerConfiguration, cancellationToken)
            .ConfigureAwait(false);

        TaxpayerConfigurationDto? existing = null;
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                existing = JsonSerializer.Deserialize<TaxpayerConfigurationDto>(existingJson, MraJson.SerializerOptions);
            }
            catch (JsonException)
            {
                existing = null;
            }
        }

        if (existing is not null &&
            !string.IsNullOrWhiteSpace(existing.Tin) &&
            !existing.Tin.Trim().Equals("1234567890", StringComparison.Ordinal))
        {
            return;
        }

        var taxpayer = existing ?? new TaxpayerConfigurationDto
        {
            VersionNo = 1,
            IsVatRegistered = true,
            ActivatedTaxRateIds = ["A"]
        };
        taxpayer.Tin = tin.Trim();
        if (taxpayer.VersionNo <= 0)
        {
            taxpayer.VersionNo = 1;
        }

        await _configurationRepository.UpsertJsonAsync(
                MraConfigurationKeys.TaxpayerConfiguration,
                JsonSerializer.Serialize(taxpayer, MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);

        await _configurationRepository.UpsertJsonAsync(
                DeploymentConfigurationKeys.TaxpayerTin,
                JsonSerializer.Serialize(new { tin = tin.Trim() }, MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Seeded sellerTIN={Tin} from activation JWT claim after config sync failure.", tin);
    }

    private async Task CacheConfigurationBundleAsync(
        EisConfigurationBundleDto bundle,
        CancellationToken cancellationToken)
    {
        if (bundle.GlobalConfiguration is not null)
        {
            await _configurationRepository.UpsertJsonAsync(
                MraConfigurationKeys.GlobalConfiguration,
                JsonSerializer.Serialize(bundle.GlobalConfiguration, MraJson.SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }

        if (bundle.TerminalConfiguration is not null)
        {
            await _configurationRepository.UpsertJsonAsync(
                MraConfigurationKeys.TerminalConfiguration,
                JsonSerializer.Serialize(bundle.TerminalConfiguration, MraJson.SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }

        if (bundle.TaxpayerConfiguration is not null)
        {
            await _configurationRepository.UpsertJsonAsync(
                MraConfigurationKeys.TaxpayerConfiguration,
                JsonSerializer.Serialize(bundle.TaxpayerConfiguration, MraJson.SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class TerminalActivationResult
{
    public bool Success { get; init; }
    public string? TerminalId { get; init; }
    public string? Remark { get; init; }
    public int StatusCode { get; init; }
    public string? OperatorMessage { get; init; }
    public EisConfigurationBundleDto? Configuration { get; init; }
    public IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? Errors { get; init; }

    public static TerminalActivationResult Succeeded(
        string terminalId,
        string? remark,
        EisConfigurationBundleDto? configuration) =>
        new()
        {
            Success = true,
            TerminalId = terminalId,
            Remark = remark,
            StatusCode = 1,
            Configuration = configuration
        };

    public static TerminalActivationResult Failed(
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors,
        int statusCode = 0,
        string? operatorMessage = null) =>
        new()
        {
            Success = false,
            Remark = remark,
            Errors = errors,
            StatusCode = statusCode,
            OperatorMessage = operatorMessage ?? remark
        };

    public static TerminalActivationResult FailedFromEvaluation(MraEisResponseEvaluation evaluation) =>
        new()
        {
            Success = false,
            Remark = evaluation.Remark ?? evaluation.TechnicalDetail,
            Errors = evaluation.Errors.Count == 0 ? null : evaluation.Errors,
            StatusCode = evaluation.StatusCode,
            OperatorMessage = evaluation.OperatorMessage
        };
}

public sealed class TerminalConfirmationResult
{
    public bool Success { get; init; }
    public string? TerminalId { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? Errors { get; init; }

    public static TerminalConfirmationResult Succeeded(string terminalId, string? remark) =>
        new() { Success = true, TerminalId = terminalId, Remark = remark };

    public static TerminalConfirmationResult Failed(
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors) =>
        new() { Success = false, Remark = remark, Errors = errors };
}

public sealed class LatestConfigurationResult
{
    public bool Success { get; init; }

    /// <summary>
    /// True when live EIS sync failed (e.g. HTTP 500) but local <c>dbo.Terminals</c> activation
    /// and stored credentials / cached configs were used instead.
    /// </summary>
    public bool UsedLocalFallback { get; init; }

    /// <summary>Live sync succeeded, or local activation fallback is usable for offline/test flows.</summary>
    public bool IsUsable => Success || UsedLocalFallback;

    public EisConfigurationBundleDto? Configuration { get; init; }
    public string? Remark { get; init; }
    public int? HttpStatusCode { get; init; }
    public IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? Errors { get; init; }

    public static LatestConfigurationResult Succeeded(EisConfigurationBundleDto configuration, string? remark) =>
        new() { Success = true, Configuration = configuration, Remark = remark };

    public static LatestConfigurationResult SucceededFromLocalFallback(
        EisConfigurationBundleDto configuration,
        string? remark,
        int? httpStatusCode = null) =>
        new()
        {
            Success = false,
            UsedLocalFallback = true,
            Configuration = configuration,
            Remark = remark,
            HttpStatusCode = httpStatusCode
        };

    public static LatestConfigurationResult Failed(
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors,
        int? httpStatusCode = null) =>
        new()
        {
            Success = false,
            Remark = remark,
            Errors = errors,
            HttpStatusCode = httpStatusCode
        };
}
