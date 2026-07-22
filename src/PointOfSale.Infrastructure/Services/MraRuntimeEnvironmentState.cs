using PointOfSale.Mra.Options;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Runtime MRA EIS endpoint and compliance state (production handshake, sync, certificate).
/// </summary>
public sealed class MraRuntimeEnvironmentState
{
    private readonly object _sync = new();

    public string? EnvironmentOverride { get; private set; }
    public DateTime? LastHandshakeUtc { get; private set; }
    public DateTime? LastSuccessfulSyncUtc { get; private set; }
    public DateTime? CertificateNotAfterUtc { get; private set; }
    public bool FiscalLockoutActive { get; private set; }
    public string? LastCertificateWarning { get; private set; }

    public bool IsLiveProductionActive(MraApiOptions options) =>
        string.Equals(GetEffectiveEnvironment(options), "Production", StringComparison.OrdinalIgnoreCase);

    public string GetEffectiveEnvironment(MraApiOptions options) =>
        EnvironmentOverride ?? options.Environment;

    public string GetEffectiveBaseUrl(MraApiOptions options)
    {
        var clone = new MraApiOptions
        {
            Environment = GetEffectiveEnvironment(options),
            SandboxBaseUrl = options.SandboxBaseUrl,
            ProductionBaseUrl = options.ProductionBaseUrl,
            BaseUrl = options.BaseUrl,
            ProductId = options.ProductId,
            ProductVersion = options.ProductVersion,
            HttpTimeout = options.HttpTimeout
        };
        return clone.ResolveBaseUrl();
    }

    public void ApplyHandshake(string environment, DateTime handshakeUtc, DateTime? certificateNotAfterUtc)
    {
        lock (_sync)
        {
            EnvironmentOverride = environment;
            LastHandshakeUtc = handshakeUtc;
            CertificateNotAfterUtc = certificateNotAfterUtc;
            FiscalLockoutActive = false;
            LastCertificateWarning = null;
        }
    }

    public void RecordSuccessfulSync(DateTime syncUtc)
    {
        lock (_sync)
        {
            LastSuccessfulSyncUtc = syncUtc;
        }
    }

    public void SetCertificateExpiry(DateTime? notAfterUtc, string? warning, bool lockout)
    {
        lock (_sync)
        {
            CertificateNotAfterUtc = notAfterUtc;
            LastCertificateWarning = warning;
            FiscalLockoutActive = lockout;
        }
    }

    public void LoadFromPersisted(
        string? environment,
        DateTime? handshakeUtc,
        DateTime? syncUtc,
        DateTime? certExpiry,
        bool lockout)
    {
        lock (_sync)
        {
            EnvironmentOverride = string.IsNullOrWhiteSpace(environment) ? null : environment;
            LastHandshakeUtc = handshakeUtc;
            LastSuccessfulSyncUtc = syncUtc;
            CertificateNotAfterUtc = certExpiry;
            FiscalLockoutActive = lockout;
        }
    }
}
