using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface ITerminalActivationService
{
    event EventHandler? ActivationStatusChanged;

    bool IsActivated { get; }
    DateTime? ActivatedAtUtc { get; }
    string? MaskedLicenseKey { get; }
    string StatusText { get; }

    Task<TerminalLicenseStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<TerminalActivationResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default);
    bool ValidateLicenseKeyFormat(string licenseKey, out string normalized, out string? error);
    bool AcceptsLicenseKey(string licenseKey);
}

public sealed class TerminalLicenseOptions
{
    public const string SectionName = "TerminalLicense";

    /// <summary>When false (lab only), retail functions are available without a license key.</summary>
    public bool RequireActivation { get; set; } = true;

    /// <summary>Extra allow-listed keys (uppercase, dashed). Prefer issuing checksum-valid keys.</summary>
    public string[] AdditionalValidKeys { get; set; } = [];

    /// <summary>Pepper mixed into license checksum generation (change per OEM build).</summary>
    public string VerificationPepper { get; set; } = "AlbertRetailTerminal.License.v1";
}

public sealed class TerminalLicenseStatus
{
    public bool RequireActivation { get; init; }
    public bool IsActivated { get; init; }
    public DateTime? ActivatedAtUtc { get; init; }
    public string? MaskedLicenseKey { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool RegistryMirrorPresent { get; init; }
}

public sealed class TerminalActivationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static TerminalActivationResult Ok(string message) => new() { Success = true, Message = message };
    public static TerminalActivationResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// Validates Albert Retail Terminal software license keys (XXXX-XXXX-XXXX-XXXX),
/// persists activation in SQL Express + HKCU registry, and gates retail use until activated.
/// </summary>
public sealed class TerminalActivationService : ITerminalActivationService
{
    public const string ConfigActivatedKey = "Terminal.License.Activated";
    public const string ConfigPayloadKey = "Terminal.License.Payload";

    private const string RegistryRoot = @"Software\AlbertRetail\AlbertRetailTerminal";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TerminalLicenseOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TerminalActivationService> _logger;

    private bool _isActivated;
    private DateTime? _activatedAtUtc;
    private string? _maskedKey;
    private string _statusText = "Checking license…";

    public TerminalActivationService(
        IOptions<TerminalLicenseOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<TerminalActivationService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public event EventHandler? ActivationStatusChanged;

    public bool IsActivated => !_options.RequireActivation || _isActivated;
    public DateTime? ActivatedAtUtc => _activatedAtUtc;
    public string? MaskedLicenseKey => _maskedKey;
    public string StatusText => _statusText;

    public async Task<TerminalLicenseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.RequireActivation)
        {
            _isActivated = true;
            _statusText = "License activation bypassed (RequireActivation=false).";
            RaiseChanged();
            return Snapshot(registryPresent: false);
        }

        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();

        var activatedJson = await config.GetJsonAsync(ConfigActivatedKey, cancellationToken).ConfigureAwait(false);
        var payloadJson = await config.GetJsonAsync(ConfigPayloadKey, cancellationToken).ConfigureAwait(false);
        var dbActivated = IsTruthy(activatedJson);

        LicensePayload? payload = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                payload = JsonSerializer.Deserialize<LicensePayload>(payloadJson, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Corrupt terminal license payload in Configurations.");
            }
        }

        var registry = ReadRegistryMirror();
        var registryOk = registry is { Activated: true } && !string.IsNullOrWhiteSpace(registry.KeyHash);

        if (dbActivated && payload is not null && !string.IsNullOrWhiteSpace(payload.KeyHash))
        {
            _isActivated = true;
            _activatedAtUtc = payload.ActivatedAtUtc;
            _maskedKey = payload.MaskedKey;
            _statusText = "Terminal license active.";
            if (!registryOk)
            {
                WriteRegistryMirror(payload.KeyHash, payload.MaskedKey, payload.ActivatedAtUtc);
            }
        }
        else if (registryOk && registry is not null)
        {
            _isActivated = true;
            _activatedAtUtc = registry.ActivatedAtUtc;
            _maskedKey = registry.MaskedKey;
            _statusText = "Terminal license restored from secure registry mirror.";
            await PersistAsync(
                    config,
                    registry.KeyHash!,
                    registry.MaskedKey ?? "****",
                    registry.ActivatedAtUtc ?? DateTime.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _isActivated = false;
            _activatedAtUtc = null;
            _maskedKey = null;
            _statusText = "Activation required — enter a valid license key to unlock retail functions.";
        }

        RaiseChanged();
        return Snapshot(registryOk);
    }

    public async Task<TerminalActivationResult> ActivateAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        if (!_options.RequireActivation)
        {
            _isActivated = true;
            RaiseChanged();
            return TerminalActivationResult.Ok("Activation not required in this environment.");
        }

        if (!ValidateLicenseKeyFormat(licenseKey, out var normalized, out var error))
        {
            return TerminalActivationResult.Fail(error ?? "Invalid license key format.");
        }

        if (!IsLicenseKeyAccepted(normalized))
        {
            _logger.LogWarning("Rejected terminal license key {Masked}.", MaskKey(normalized));
            return TerminalActivationResult.Fail(
                "License key is not valid. Check the key and try again (format XXXX-XXXX-XXXX-XXXX).");
        }

        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();

        var hash = ComputeKeyHash(normalized);
        var activatedAt = DateTime.UtcNow;
        var masked = MaskKey(normalized);
        await PersistAsync(config, hash, masked, activatedAt, cancellationToken).ConfigureAwait(false);
        WriteRegistryMirror(hash, masked, activatedAt);

        _isActivated = true;
        _activatedAtUtc = activatedAt;
        _maskedKey = masked;
        _statusText = "Terminal activated successfully.";
        RaiseChanged();
        _logger.LogInformation("Terminal license activated ({Masked}).", masked);
        return TerminalActivationResult.Ok("Terminal activated. You can now sign in.");
    }

    public bool ValidateLicenseKeyFormat(string licenseKey, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            error = "License key is required.";
            return false;
        }

        var cleaned = LicenseKeyInputFormatter.ApplyMask(licenseKey);
        if (!LicenseKeyInputFormatter.IsExactFormat(cleaned))
        {
            error = LicenseKeyInputFormatter.FormatErrorMessage;
            return false;
        }

        normalized = cleaned;
        return true;
    }

    public bool AcceptsLicenseKey(string licenseKey)
    {
        if (!ValidateLicenseKeyFormat(licenseKey, out var normalized, out _))
        {
            return false;
        }

        return IsLicenseKeyAccepted(normalized);
    }

    internal bool IsLicenseKeyAccepted(string normalizedKey)
    {
        foreach (var extra in _options.AdditionalValidKeys ?? [])
        {
            if (ValidateLicenseKeyFormat(extra, out var n, out _)
                && string.Equals(n, normalizedKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return VerifyChecksumGroup(normalizedKey, _options.VerificationPepper);
    }

    /// <summary>
    /// Checksum: SHA256(G1G2G3|pepper) → first 4 Base36 chars must equal G4.
    /// </summary>
    public static bool VerifyChecksumGroup(string normalizedKey, string pepper)
    {
        var parts = normalizedKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var expected = ComputeChecksumGroup(parts[0] + parts[1] + parts[2], pepper);
        return string.Equals(expected, parts[3], StringComparison.Ordinal);
    }

    public static string ComputeChecksumGroup(string payload12, string pepper)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload12 + "|" + pepper));
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    public static string MaskKey(string normalizedKey)
    {
        var parts = normalizedKey.Split('-');
        if (parts.Length != 4)
        {
            return "****-****-****-****";
        }

        return $"{parts[0]}-****-****-{parts[3]}";
    }

    public static string ComputeKeyHash(string normalizedKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("ART-LICENSE|" + normalizedKey));
        return Convert.ToHexString(hash);
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim().Trim('"');
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || trimmed == "1";
    }

    private static async Task PersistAsync(
        IConfigurationRepository config,
        string keyHash,
        string masked,
        DateTime activatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = new LicensePayload
        {
            KeyHash = keyHash,
            MaskedKey = masked,
            ActivatedAtUtc = activatedAtUtc
        };
        await config.UpsertJsonAsync(ConfigActivatedKey, "true", cancellationToken).ConfigureAwait(false);
        await config.UpsertJsonAsync(
                ConfigPayloadKey,
                JsonSerializer.Serialize(payload, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void WriteRegistryMirror(string keyHash, string masked, DateTime activatedAtUtc)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRoot);
            if (key is null)
            {
                return;
            }

            key.SetValue("Activated", 1, RegistryValueKind.DWord);
            key.SetValue("KeyHash", keyHash, RegistryValueKind.String);
            key.SetValue("MaskedKey", masked, RegistryValueKind.String);
            key.SetValue("ActivatedAtUtc", activatedAtUtc.ToString("O"), RegistryValueKind.String);
        }
        catch
        {
            // Registry is a mirror only; SQL remains authoritative.
        }
    }

    private static RegistryMirror? ReadRegistryMirror()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRoot);
            if (key is null)
            {
                return null;
            }

            var activated = Convert.ToInt32(key.GetValue("Activated", 0)) == 1;
            var hash = key.GetValue("KeyHash") as string;
            var masked = key.GetValue("MaskedKey") as string;
            DateTime? at = null;
            if (key.GetValue("ActivatedAtUtc") is string s
                && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                at = parsed.ToUniversalTime();
            }

            return new RegistryMirror
            {
                Activated = activated,
                KeyHash = hash,
                MaskedKey = masked,
                ActivatedAtUtc = at
            };
        }
        catch
        {
            return null;
        }
    }

    private TerminalLicenseStatus Snapshot(bool registryPresent) => new()
    {
        RequireActivation = _options.RequireActivation,
        IsActivated = IsActivated,
        ActivatedAtUtc = _activatedAtUtc,
        MaskedLicenseKey = _maskedKey,
        StatusText = _statusText,
        RegistryMirrorPresent = registryPresent
    };

    private void RaiseChanged() => ActivationStatusChanged?.Invoke(this, EventArgs.Empty);

    private sealed class LicensePayload
    {
        public string KeyHash { get; set; } = string.Empty;
        public string MaskedKey { get; set; } = string.Empty;
        public DateTime ActivatedAtUtc { get; set; }
    }

    private sealed class RegistryMirror
    {
        public bool Activated { get; init; }
        public string? KeyHash { get; init; }
        public string? MaskedKey { get; init; }
        public DateTime? ActivatedAtUtc { get; init; }
    }
}
