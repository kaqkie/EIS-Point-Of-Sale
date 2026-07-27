using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Compliance;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Options;

namespace PointOfSale.App.Services;

public sealed class MraHandshakeStatus
{
    public bool IsLiveProductionActive { get; init; }
    public DateTime? CertificateExpirationDateUtc { get; init; }
    public DateTime? LastSuccessfulMraSyncUtc { get; init; }
    public DateTime? LastHandshakeUtc { get; init; }
    public string TamperCheckStatus { get; init; } = "Unknown";
    public bool FiscalLockoutActive { get; init; }
    public string? CertificateWarning { get; init; }
    public string EffectiveBaseUrl { get; init; } = string.Empty;
}

public interface IMraProductionHandshakeService
{
    Task<MraHandshakeStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<MraHandshakeStatus> ActivateProductionHandshakeAsync(CancellationToken cancellationToken = default);

    Task<MraHandshakeStatus> RenewCredentialsAsync(CancellationToken cancellationToken = default);

    Task<MraHandshakeStatus> ValidateCertificateChainAsync(CancellationToken cancellationToken = default);

    Task<string> ExportStatutoryComplianceLogAsync(CancellationToken cancellationToken = default);
}

public sealed class MraProductionHandshakeService : IMraProductionHandshakeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MraRuntimeEnvironmentState _runtimeState;
    private readonly IComplianceAuditLogger _complianceAudit;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly IOptions<MraApiOptions> _mraOptions;
    private readonly MraProductionHandshakeOptions _handshakeOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MraProductionHandshakeService> _logger;

    public MraProductionHandshakeService(
        IServiceScopeFactory scopeFactory,
        MraRuntimeEnvironmentState runtimeState,
        IComplianceAuditLogger complianceAudit,
        IAuthenticationAuthorizationService auth,
        IOptions<MraApiOptions> mraOptions,
        IOptions<MraProductionHandshakeOptions> handshakeOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<MraProductionHandshakeService> logger)
    {
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _complianceAudit = complianceAudit;
        _auth = auth;
        _mraOptions = mraOptions;
        _handshakeOptions = handshakeOptions.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<MraHandshakeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await LoadPersistedStateAsync(cancellationToken).ConfigureAwait(false);
        var tamper = await _complianceAudit.VerifyChainAsync(cancellationToken).ConfigureAwait(false);
        return BuildStatus(tamper);
    }

    public async Task<MraHandshakeStatus> ActivateProductionHandshakeAsync(CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.AccessCompliance);

        await EnsureDpapiCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var jwt = await WithConfigurationRepository(async (repo, ct) =>
                await repo.GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, ct).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("DPAPI-protected JWT is required before production handshake.");

        var productionUrl = new MraApiOptions
        {
            Environment = "Production",
            ProductionBaseUrl = _mraOptions.Value.ProductionBaseUrl,
            SandboxBaseUrl = _mraOptions.Value.SandboxBaseUrl
        }.ResolveBaseUrl();

        await ValidateTlsChainAsync(productionUrl, cancellationToken).ConfigureAwait(false);

        var certExpiry = JwtExpiryParser.TryGetExpiryUtc(jwt);
        var handshakeUtc = DateTime.UtcNow;

        await PersistAsync(
                "Production",
                handshakeUtc,
                _runtimeState.LastSuccessfulSyncUtc,
                certExpiry,
                lockout: false,
                cancellationToken)
            .ConfigureAwait(false);

        _runtimeState.ApplyHandshake("Production", handshakeUtc, certExpiry);

        await _complianceAudit.LogEventAsync(
                ComplianceAuditCategories.MraHandshake,
                "ActivateProduction",
                $"Production handshake completed against {productionUrl}.",
                success: true,
                operatorUsername: _auth.CurrentOperator?.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("MRA production handshake activated for terminal.");
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MraHandshakeStatus> RenewCredentialsAsync(CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.AccessCompliance);

        if (_runtimeState.FiscalLockoutActive)
        {
            throw new InvalidOperationException("Fiscal lockout is active. Resolve certificate warnings before renewal.");
        }

        using var scope = _scopeFactory.CreateScope();
        var onboarding = scope.ServiceProvider.GetRequiredService<TerminalOnboardingService>();
        var result = await onboarding.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await _complianceAudit.LogEventAsync(
                    ComplianceAuditCategories.Certificate,
                    "RenewCredentials",
                    result.Remark ?? "Configuration refresh failed.",
                    success: false,
                    operatorUsername: _auth.CurrentOperator?.Username,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(result.Remark ?? "MRA credential renewal failed.");
        }

        var jwt = await WithConfigurationRepository(async (repo, ct) =>
                await repo.GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, ct).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        var certExpiry = JwtExpiryParser.TryGetExpiryUtc(jwt);
        await PersistAsync(
                _runtimeState.GetEffectiveEnvironment(_mraOptions.Value),
                _runtimeState.LastHandshakeUtc ?? DateTime.UtcNow,
                DateTime.UtcNow,
                certExpiry,
                lockout: false,
                cancellationToken)
            .ConfigureAwait(false);

        _runtimeState.SetCertificateExpiry(certExpiry, warning: null, lockout: false);
        _runtimeState.RecordSuccessfulSync(DateTime.UtcNow);

        await _complianceAudit.LogEventAsync(
                ComplianceAuditCategories.Certificate,
                "RenewCredentials",
                "MRA terminal credentials and configuration cache refreshed.",
                success: true,
                operatorUsername: _auth.CurrentOperator?.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return await ValidateCertificateChainAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MraHandshakeStatus> ValidateCertificateChainAsync(CancellationToken cancellationToken = default)
    {
        await LoadPersistedStateAsync(cancellationToken).ConfigureAwait(false);

        var jwt = await WithConfigurationRepository(async (repo, ct) =>
                await repo.GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, ct).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        var expiry = JwtExpiryParser.TryGetExpiryUtc(jwt) ?? _runtimeState.CertificateNotAfterUtc;
        string? warning = null;
        var lockout = false;

        if (expiry is not null)
        {
            var days = (expiry.Value - DateTime.UtcNow).TotalDays;
            if (days < 0 || (_handshakeOptions.CertificateLockoutDays > 0 && days <= _handshakeOptions.CertificateLockoutDays))
            {
                lockout = true;
                warning = $"Fiscal signing token expired or expires in {days:0} day(s). Live submissions locked.";
            }
            else if (days <= _handshakeOptions.CertificateWarningDays)
            {
                warning = $"Fiscal signing token expires on {expiry.Value:u} ({days:0} day(s) remaining).";
            }
        }

        var baseUrl = _runtimeState.GetEffectiveBaseUrl(_mraOptions.Value);
        try
        {
            await ValidateTlsChainAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            warning = string.IsNullOrWhiteSpace(warning) ? ex.Message : $"{warning} TLS: {ex.Message}";
            lockout = _runtimeState.IsLiveProductionActive(_mraOptions.Value);
        }

        _runtimeState.SetCertificateExpiry(expiry, warning, lockout);
        await PersistAsync(
                _runtimeState.GetEffectiveEnvironment(_mraOptions.Value),
                _runtimeState.LastHandshakeUtc,
                _runtimeState.LastSuccessfulSyncUtc,
                expiry,
                lockout,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(warning))
        {
            await _complianceAudit.LogEventAsync(
                    ComplianceAuditCategories.Certificate,
                    lockout ? "CertificateLockout" : "CertificateWarning",
                    warning,
                    success: !lockout,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportStatutoryComplianceLogAsync(CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.AccessCompliance);

        var entries = await _complianceAudit.GetRecentAsync(5000, cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(AppContext.BaseDirectory, "Exports", "Compliance");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"compliance-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");

        var builder = new StringBuilder();
        builder.AppendLine("EntryId,CreatedAtUtc,Category,Action,Operator,Success,CorrelationId,Detail,PreviousHash,EntryHash");
        foreach (var row in entries.OrderBy(e => e.EntryId))
        {
            builder.Append(row.EntryId).Append(',')
                .Append(row.CreatedAtUtc.ToString("O")).Append(',')
                .Append(Csv(row.Category)).Append(',')
                .Append(Csv(row.Action)).Append(',')
                .Append(Csv(row.OperatorUsername)).Append(',')
                .Append(row.Success).Append(',')
                .Append(Csv(row.CorrelationId)).Append(',')
                .Append(Csv(row.Detail)).Append(',')
                .Append(row.PreviousHash).Append(',')
                .AppendLine(row.EntryHash);
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);

        await _complianceAudit.LogEventAsync(
                ComplianceAuditCategories.SupervisorAuth,
                "ExportStatutoryLog",
                $"Exported {entries.Count} compliance rows to {path}.",
                success: true,
                operatorUsername: _auth.CurrentOperator?.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return path;
    }

    private MraHandshakeStatus BuildStatus(ComplianceTamperCheckResult tamper) =>
        new()
        {
            IsLiveProductionActive = _runtimeState.IsLiveProductionActive(_mraOptions.Value),
            CertificateExpirationDateUtc = _runtimeState.CertificateNotAfterUtc,
            LastSuccessfulMraSyncUtc = _runtimeState.LastSuccessfulSyncUtc,
            LastHandshakeUtc = _runtimeState.LastHandshakeUtc,
            TamperCheckStatus = tamper.IsValid ? tamper.Message : $"FAILED: {tamper.Message}",
            FiscalLockoutActive = _runtimeState.FiscalLockoutActive,
            CertificateWarning = _runtimeState.LastCertificateWarning,
            EffectiveBaseUrl = _runtimeState.GetEffectiveBaseUrl(_mraOptions.Value)
        };

    private async Task EnsureDpapiCredentialsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var terminalRepository = scope.ServiceProvider.GetRequiredService<ITerminalRepository>();
        var terminalId = await terminalRepository.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            throw new InvalidOperationException("Terminal is not activated.");
        }

        var jwt = await WithConfigurationRepository(async (repo, ct) =>
                await repo.GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, ct).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException("JWT missing from DPAPI configuration store.");
        }
    }

    private async Task ValidateTlsChainAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (!_handshakeOptions.Enabled)
        {
            return;
        }

        using var client = _httpClientFactory.CreateClient(nameof(MraProductionHandshakeService));
        client.Timeout = TimeSpan.FromSeconds(_handshakeOptions.HttpTimeoutSeconds);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var handler = new HttpClientHandler();
        var chainOk = false;
        handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
        {
            chainOk = errors == SslPolicyErrors.None && chain is not null && chain.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError);
            if (!chainOk && cert is not null)
            {
                _logger.LogWarning("TLS chain validation issues for {Subject}: {Errors}", cert.Subject, errors);
            }

            return errors == SslPolicyErrors.None;
        };

        using var validatingClient = new HttpClient(handler) { Timeout = client.Timeout };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(baseUrl), "configuration/get-latest-configs"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        using var response = await validatingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        _ = response.StatusCode;
    }

    private async Task LoadPersistedStateAsync(CancellationToken cancellationToken)
    {
        var env = await ReadConfigStringAsync(MraRuntimeConfigurationKeys.RuntimeEnvironment, cancellationToken)
            .ConfigureAwait(false);
        var handshake = await ReadConfigDateAsync(MraRuntimeConfigurationKeys.LastHandshakeUtc, cancellationToken)
            .ConfigureAwait(false);
        var sync = await ReadConfigDateAsync(MraRuntimeConfigurationKeys.LastSuccessfulSyncUtc, cancellationToken)
            .ConfigureAwait(false);
        var cert = await ReadConfigDateAsync(MraRuntimeConfigurationKeys.CertificateNotAfterUtc, cancellationToken)
            .ConfigureAwait(false);
        var lockout = string.Equals(
            await ReadConfigStringAsync(MraRuntimeConfigurationKeys.FiscalLockoutActive, cancellationToken).ConfigureAwait(false),
            "true",
            StringComparison.OrdinalIgnoreCase);

        _runtimeState.LoadFromPersisted(env, handshake, sync, cert, lockout);
    }

    private async Task PersistAsync(
        string environment,
        DateTime? handshakeUtc,
        DateTime? syncUtc,
        DateTime? certExpiry,
        bool lockout,
        CancellationToken cancellationToken)
    {
        await WriteConfigStringAsync(MraRuntimeConfigurationKeys.RuntimeEnvironment, environment, cancellationToken)
            .ConfigureAwait(false);
        if (handshakeUtc is not null)
        {
            await WriteConfigStringAsync(MraRuntimeConfigurationKeys.LastHandshakeUtc, handshakeUtc.Value.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
        }

        if (syncUtc is not null)
        {
            await WriteConfigStringAsync(MraRuntimeConfigurationKeys.LastSuccessfulSyncUtc, syncUtc.Value.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
        }

        if (certExpiry is not null)
        {
            await WriteConfigStringAsync(MraRuntimeConfigurationKeys.CertificateNotAfterUtc, certExpiry.Value.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteConfigStringAsync(
                MraRuntimeConfigurationKeys.FiscalLockoutActive,
                lockout ? "true" : "false",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> ReadConfigStringAsync(string key, CancellationToken cancellationToken) =>
        await WithConfigurationRepository(async (repo, ct) =>
        {
            var json = await repo.GetJsonAsync(key, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<string>(json);
            }
            catch
            {
                return json.Trim('"');
            }
        }, cancellationToken).ConfigureAwait(false);

    private async Task<DateTime?> ReadConfigDateAsync(string key, CancellationToken cancellationToken)
    {
        var raw = await ReadConfigStringAsync(key, cancellationToken).ConfigureAwait(false);
        return DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;
    }

    private async Task WriteConfigStringAsync(string key, string value, CancellationToken cancellationToken)
    {
        await WithConfigurationRepository(
            async (repo, ct) =>
            {
                await repo.UpsertJsonAsync(key, JsonSerializer.Serialize(value), ct).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithConfigurationRepository<T>(
        Func<IConfigurationRepository, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        return await action(repo, cancellationToken).ConfigureAwait(false);
    }

    private static string Csv(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n'))
        {
            return $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return v;
    }
}

internal static class JwtExpiryParser
{
    public static DateTime? TryGetExpiryUtc(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1];
            var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}

public sealed class MraProductionHandshakeMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MraProductionHandshakeOptions _options;
    private readonly ILogger<MraProductionHandshakeMonitor> _logger;

    public MraProductionHandshakeMonitor(
        IServiceScopeFactory scopeFactory,
        IOptions<MraProductionHandshakeOptions> options,
        ILogger<MraProductionHandshakeMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, _options.ValidationIntervalMinutes));
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handshake = scope.ServiceProvider.GetRequiredService<IMraProductionHandshakeService>();
                await handshake.ValidateCertificateChainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MRA certificate validation loop failed.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
