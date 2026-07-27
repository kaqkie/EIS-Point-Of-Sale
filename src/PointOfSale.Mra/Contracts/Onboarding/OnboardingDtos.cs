using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Configuration;

namespace PointOfSale.Mra.Contracts.Onboarding;

public sealed class ActivateTerminalRequest
{
    [JsonPropertyName("terminalActivationCode")]
    public required string TerminalActivationCode { get; init; }

    [JsonPropertyName("environment")]
    public required TerminalEnvironmentDto Environment { get; init; }
}

public sealed class TerminalEnvironmentDto
{
    [JsonPropertyName("platform")]
    public required PlatformEnvironmentDto Platform { get; init; }

    [JsonPropertyName("pos")]
    public required PosEnvironmentDto Pos { get; init; }
}

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

public sealed class PosEnvironmentDto
{
    [JsonPropertyName("productID")]
    public required string ProductId { get; init; }

    [JsonPropertyName("productVersion")]
    public required string ProductVersion { get; init; }
}

public sealed class ActivateTerminalResponseData
{
    [JsonPropertyName("activatedTerminal")]
    public ActivatedTerminalDto? ActivatedTerminal { get; set; }

    [JsonPropertyName("configuration")]
    public EisConfigurationBundleDto? Configuration { get; set; }
}

public sealed class ActivatedTerminalDto
{
    [JsonPropertyName("terminalId")]
    public string? TerminalId { get; set; }

    [JsonPropertyName("terminalPosition")]
    public int? TerminalPosition { get; set; }

    [JsonPropertyName("activationDate")]
    public DateTimeOffset? ActivationDate { get; set; }

    [JsonPropertyName("terminalCredentials")]
    public TerminalCredentialsDto? TerminalCredentials { get; set; }
}

public sealed class TerminalCredentialsDto
{
    [JsonPropertyName("jwtToken")]
    public string? JwtToken { get; set; }

    [JsonPropertyName("secretKey")]
    public string? SecretKey { get; set; }
}

public sealed class TerminalActivatedConfirmationRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; init; }
}

public sealed class TerminalActivatedConfirmationResponseData
{
    [JsonPropertyName("terminalId")]
    public string? TerminalId { get; set; }

    [JsonPropertyName("isActivated")]
    public bool IsActivated { get; set; }
}
