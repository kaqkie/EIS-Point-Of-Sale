using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Models;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Onboarding;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Serialization;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;

namespace PointOfSale.Infrastructure.Services;

public sealed class TerminalOnboardingService
{
    private readonly MraApiClient _apiClient;
    private readonly ITerminalRepository _terminalRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly MraApiOptions _options;
    private readonly ILogger<TerminalOnboardingService> _logger;

    public TerminalOnboardingService(
        MraApiClient apiClient,
        ITerminalRepository terminalRepository,
        IConfigurationRepository configurationRepository,
        ISecretProtector secretProtector,
        IOptions<MraApiOptions> options,
        ILogger<TerminalOnboardingService> logger)
    {
        _apiClient = apiClient;
        _terminalRepository = terminalRepository;
        _configurationRepository = configurationRepository;
        _secretProtector = secretProtector;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TerminalActivationResult> ActivateTerminalAsync(
        TerminalActivationRequest request,
        CancellationToken cancellationToken = default)
    {
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

        var response = await _apiClient
            .PostAsync<ActivateTerminalRequest, ActivateTerminalResponseData>(
                "onboarding/activate-terminal",
                apiRequest,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Data?.ActivatedTerminal?.TerminalCredentials is null)
        {
            return TerminalActivationResult.Failed(response.Remark, response.Errors);
        }

        var activated = response.Data.ActivatedTerminal;
        var credentials = activated.TerminalCredentials!;
        var terminalId = activated.TerminalId
            ?? throw new InvalidOperationException("MRA activation response did not include terminalId.");

        if (string.IsNullOrWhiteSpace(credentials.SecretKey) || string.IsNullOrWhiteSpace(credentials.JwtToken))
        {
            throw new InvalidOperationException("MRA activation response missing jwtToken or secretKey.");
        }

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
            credentials.JwtToken,
            cancellationToken).ConfigureAwait(false);

        await _configurationRepository.UpsertProtectedSecretAsync(
            MraConfigurationKeys.PendingSecretKey,
            credentials.SecretKey,
            cancellationToken).ConfigureAwait(false);

        await _configurationRepository.UpsertJsonAsync(
            MraConfigurationKeys.TerminalActivationCode,
            JsonSerializer.Serialize(new { code = request.TerminalActivationCode.Trim() }, MraJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        if (response.Data.Configuration is not null)
        {
            await CacheConfigurationBundleAsync(response.Data.Configuration, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Terminal {TerminalId} activated pending MRA confirmation.", terminalId);

        return TerminalActivationResult.Succeeded(terminalId, response.Remark, response.Data.Configuration);
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

        var body = new TerminalActivatedConfirmationRequest { TerminalId = request.TerminalId.Trim() };
        var tac = request.TerminalActivationCode.Trim();

        var response = await _apiClient
            .PostAsync<TerminalActivatedConfirmationRequest, TerminalActivatedConfirmationResponseData>(
                "onboarding/terminal-activated-confirmation",
                body,
                new MraRequestContext
                {
                    SecretKey = pendingSecret,
                    SignaturePlainText = tac
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return TerminalConfirmationResult.Failed(response.Remark, response.Errors);
        }

        var protectedSecret = _secretProtector.Protect(pendingSecret);
        await _terminalRepository.MarkActivatedAsync(request.TerminalId.Trim(), protectedSecret, cancellationToken)
            .ConfigureAwait(false);

        await _configurationRepository.UpsertJsonAsync(
            MraConfigurationKeys.PendingSecretKey,
            JsonSerializer.Serialize(new { cleared = true }, MraJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Terminal {TerminalId} confirmed with MRA.", request.TerminalId);

        return TerminalConfirmationResult.Succeeded(request.TerminalId, response.Remark);
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

        var response = await _apiClient
            .GetLatestConfigsAsync<GetLatestConfigurationResponseData>(jwt, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Data is null)
        {
            return LatestConfigurationResult.Failed(response.Remark, response.Errors);
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

        return LatestConfigurationResult.Succeeded(bundle, response.Remark);
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
    public EisConfigurationBundleDto? Configuration { get; init; }
    public IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? Errors { get; init; }

    public static TerminalActivationResult Succeeded(
        string terminalId,
        string? remark,
        EisConfigurationBundleDto? configuration) =>
        new() { Success = true, TerminalId = terminalId, Remark = remark, Configuration = configuration };

    public static TerminalActivationResult Failed(
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors) =>
        new() { Success = false, Remark = remark, Errors = errors };
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
    public EisConfigurationBundleDto? Configuration { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? Errors { get; init; }

    public static LatestConfigurationResult Succeeded(EisConfigurationBundleDto configuration, string? remark) =>
        new() { Success = true, Configuration = configuration, Remark = remark };

    public static LatestConfigurationResult Failed(
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors) =>
        new() { Success = false, Remark = remark, Errors = errors };
}
