using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Configuration;

/// <summary>Maps EIS <c>Configuration</c> (global / terminal / taxpayer bundle).</summary>
public sealed class EisConfigurationBundleDto
{
    [JsonPropertyName("globalConfiguration")]
    public GlobalConfigurationDto? GlobalConfiguration { get; set; }

    [JsonPropertyName("terminalConfiguration")]
    public TerminalConfigurationDto? TerminalConfiguration { get; set; }

    [JsonPropertyName("taxpayerConfiguration")]
    public TaxpayerConfigurationDto? TaxpayerConfiguration { get; set; }
}

/// <summary>Maps EIS <c>TaxConfiguration</c>.</summary>
public sealed class GlobalConfigurationDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("versionNo")]
    public decimal VersionNo { get; set; }

    [JsonPropertyName("taxrates")]
    public IReadOnlyList<TaxRateDto>? TaxRates { get; set; }
}

/// <summary>Maps EIS <c>TaxRateDto</c>.</summary>
public sealed class TaxRateDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("chargeMode")]
    public string? ChargeMode { get; set; }

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }
}

/// <summary>Maps EIS <c>TerminalConfiguration</c>.</summary>
public sealed class TerminalConfigurationDto
{
    [JsonPropertyName("versionNo")]
    public decimal VersionNo { get; set; }

    [JsonPropertyName("terminalLabel")]
    public string? TerminalLabel { get; set; }

    [JsonPropertyName("isActiveTerminal")]
    public bool? IsActiveTerminal { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("tradingName")]
    public string? TradingName { get; set; }

    [JsonPropertyName("addressLines")]
    public IReadOnlyList<string>? AddressLines { get; set; }

    [JsonPropertyName("terminalSite")]
    public TerminalSiteDto? TerminalSite { get; set; }

    [JsonPropertyName("offlineLimit")]
    public OfflineLimitDto? OfflineLimit { get; set; }
}

/// <summary>Maps EIS <c>TerminalSiteDto</c>.</summary>
public sealed class TerminalSiteDto
{
    [JsonPropertyName("siteId")]
    public string? SiteId { get; set; }

    [JsonPropertyName("siteName")]
    public string? SiteName { get; set; }
}

/// <summary>Maps EIS <c>OfflineLimit</c> (API spelling <c>maxCummulativeAmount</c>).</summary>
public sealed class OfflineLimitDto
{
    [JsonPropertyName("maxTransactionAgeInHours")]
    public decimal MaxTransactionAgeInHours { get; set; }

    [JsonPropertyName("maxCummulativeAmount")]
    public decimal MaxCummulativeAmount { get; set; }
}

/// <summary>Maps EIS <c>TaxpayerConfiguration</c>.</summary>
public sealed class TaxpayerConfigurationDto
{
    [JsonPropertyName("versionNo")]
    public decimal VersionNo { get; set; }

    [JsonPropertyName("tin")]
    public string? Tin { get; set; }

    [JsonPropertyName("isVATRegistered")]
    public bool IsVatRegistered { get; set; }

    [JsonPropertyName("taxOfficeCode")]
    public string? TaxOfficeCode { get; set; }

    [JsonPropertyName("taxOffice")]
    public TaxOfficeDto? TaxOffice { get; set; }

    [JsonPropertyName("activatedTaxRateIds")]
    public IReadOnlyList<string>? ActivatedTaxRateIds { get; set; }

    [JsonPropertyName("activatedTaxrates")]
    public IReadOnlyList<ActivatedTaxRateLinkDto>? ActivatedTaxRates { get; set; }

    [JsonPropertyName("activatedLevies")]
    public IReadOnlyList<ActivatedLevyDto>? ActivatedLevies { get; set; }
}

/// <summary>Maps EIS <c>TaxOfficeDto</c>.</summary>
public sealed class TaxOfficeDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Maps EIS <c>ActivatedTaxrateDto</c>.</summary>
public sealed class ActivatedTaxRateLinkDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("taxpayerConfigurationId")]
    public long TaxpayerConfigurationId { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }
}

/// <summary>Maps EIS <c>ActivatedLevyDto</c>.</summary>
public sealed class ActivatedLevyDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("chargeMode")]
    public string? ChargeMode { get; set; }

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}

/// <summary>Alias for get-latest-configs <c>data</c> payload (same shape as <see cref="EisConfigurationBundleDto"/>).</summary>
public sealed class GetLatestConfigurationResponseData
{
    [JsonPropertyName("globalConfiguration")]
    public GlobalConfigurationDto? GlobalConfiguration { get; set; }

    [JsonPropertyName("terminalConfiguration")]
    public TerminalConfigurationDto? TerminalConfiguration { get; set; }

    [JsonPropertyName("taxpayerConfiguration")]
    public TaxpayerConfigurationDto? TaxpayerConfiguration { get; set; }
}
