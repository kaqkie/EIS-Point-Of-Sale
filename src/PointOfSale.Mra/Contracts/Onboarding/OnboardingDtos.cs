using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Configuration;

namespace PointOfSale.Mra.Contracts.Onboarding;

/// <summary>Maps EIS <c>UnActivatedTerminal</c>.</summary>
public sealed class ActivateTerminalRequest
{
    [JsonPropertyName("terminalActivationCode")]
    public required string TerminalActivationCode { get; init; }

    [JsonPropertyName("environment")]
    public required TerminalEnvironmentDto Environment { get; init; }
}

/// <summary>Maps EIS <c>TerminalRuntimeEnvironment</c>.</summary>
public sealed class TerminalEnvironmentDto
{
    [JsonPropertyName("platform")]
    public required PlatformEnvironmentDto Platform { get; init; }

    [JsonPropertyName("pos")]
    public required PosEnvironmentDto Pos { get; init; }
}

/// <summary>Maps EIS <c>Platform</c>.</summary>
public sealed class PlatformEnvironmentDto
{
    [JsonPropertyName("osName")]
    public required string OsName { get; init; }

    [JsonPropertyName("osVersion")]
    public required string OsVersion { get; init; }

    [JsonPropertyName("osBuild")]
    public string? OsBuild { get; init; }

    [JsonPropertyName("macAddress")]
    public required string MacAddress { get; init; }
}

/// <summary>Maps EIS <c>POS</c>.</summary>
public sealed class PosEnvironmentDto
{
    [JsonPropertyName("productID")]
    public required string ProductId { get; init; }

    [JsonPropertyName("productVersion")]
    public required string ProductVersion { get; init; }
}

/// <summary>Maps EIS <c>TerminalActivationResponse</c>.</summary>
public sealed class ActivateTerminalResponseData
{
    [JsonPropertyName("activatedTerminal")]
    public ActivatedTerminalDto? ActivatedTerminal { get; set; }

    [JsonPropertyName("configuration")]
    public EisConfigurationBundleDto? Configuration { get; set; }
}

/// <summary>Maps EIS <c>ActivatedTerminal</c>.</summary>
public sealed class ActivatedTerminalDto
{
    [JsonPropertyName("terminalId")]
    public string? TerminalId { get; set; }

    [JsonPropertyName("terminalPosition")]
    public int? TerminalPosition { get; set; }

    [JsonPropertyName("taxpayerId")]
    public long? TaxpayerId { get; set; }

    [JsonPropertyName("activationDate")]
    public DateTimeOffset? ActivationDate { get; set; }

    [JsonPropertyName("terminalCredentials")]
    public TerminalCredentialsDto? TerminalCredentials { get; set; }
}

/// <summary>Maps EIS <c>TerminalCredentials</c>.</summary>
public sealed class TerminalCredentialsDto
{
    [JsonPropertyName("jwtToken")]
    public string? JwtToken { get; set; }

    [JsonPropertyName("secretKey")]
    public string? SecretKey { get; set; }
}

/// <summary>Maps EIS <c>ActivatedTerminalConfirmation</c>.</summary>
public sealed class TerminalActivatedConfirmationRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; init; }
}
