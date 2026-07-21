using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Configuration;

public sealed class EisConfigurationBundleDto
{
    [JsonPropertyName("globalConfiguration")]
    public GlobalConfigurationDto? GlobalConfiguration { get; set; }

    [JsonPropertyName("terminalConfiguration")]
    public TerminalConfigurationDto? TerminalConfiguration { get; set; }

    [JsonPropertyName("taxpayerConfiguration")]
    public TaxpayerConfigurationDto? TaxpayerConfiguration { get; set; }
}

public sealed class GlobalConfigurationDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("versionNo")]
    public int VersionNo { get; set; }

    [JsonPropertyName("taxrates")]
    public IReadOnlyList<TaxRateDto>? TaxRates { get; set; }
}

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

public sealed class TerminalConfigurationDto
{
    [JsonPropertyName("versionNo")]
    public int VersionNo { get; set; }

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

public sealed class TerminalSiteDto
{
    [JsonPropertyName("siteId")]
    public string? SiteId { get; set; }

    [JsonPropertyName("siteName")]
    public string? SiteName { get; set; }
}

public sealed class OfflineLimitDto
{
    [JsonPropertyName("maxTransactionAgeInHours")]
    public int MaxTransactionAgeInHours { get; set; }

    [JsonPropertyName("maxCummulativeAmount")]
    public decimal MaxCummulativeAmount { get; set; }
}

public sealed class TaxpayerConfigurationDto
{
    [JsonPropertyName("versionNo")]
    public int VersionNo { get; set; }

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

public sealed class TaxOfficeDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class ActivatedTaxRateLinkDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("taxpayerConfigurationId")]
    public int TaxpayerConfigurationId { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }
}

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

public sealed class GetLatestConfigurationResponseData
{
    [JsonPropertyName("globalConfiguration")]
    public GlobalConfigurationDto? GlobalConfiguration { get; set; }

    [JsonPropertyName("terminalConfiguration")]
    public TerminalConfigurationDto? TerminalConfiguration { get; set; }

    [JsonPropertyName("taxpayerConfiguration")]
    public TaxpayerConfigurationDto? TaxpayerConfiguration { get; set; }
}
