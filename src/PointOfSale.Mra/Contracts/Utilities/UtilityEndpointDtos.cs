using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Common;

namespace PointOfSale.Mra.Contracts.Utilities;

/// <summary>
/// Empty body for <c>POST /api/v1/utilities/ping</c>.
/// </summary>
public sealed class PingRequest
{
}

/// <summary>Maps EIS <c>PongResponse</c>.</summary>
public sealed class PingResponseData
{
    [JsonPropertyName("serverDate")]
    public DateTime? ServerDate { get; set; }
}

public sealed class PingResponse : EisApiResponse<PingResponseData>
{
}

/// <summary>Maps EIS <c>ProductIdentifier</c>.</summary>
public sealed class ProductStatusRequest
{
    [JsonPropertyName("productId")]
    public required string ProductId { get; init; }

    [JsonPropertyName("tin")]
    public required string Tin { get; init; }
}

/// <summary>Maps EIS <c>ProductState</c>.</summary>
public sealed class ProductStatusResponseData
{
    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("psCode")]
    public string? PsCode { get; set; }

    [JsonPropertyName("psDescription")]
    public string? PsDescription { get; set; }

    [JsonPropertyName("applicableTaxRateIds")]
    public IReadOnlyList<string>? ApplicableTaxRateIds { get; set; }

    [JsonPropertyName("quantitiesInStock")]
    public IReadOnlyList<QuantityInStockDto>? QuantitiesInStock { get; set; }
}

/// <summary>Maps EIS <c>QuantityInStock</c>.</summary>
public sealed class QuantityInStockDto
{
    [JsonPropertyName("locationId")]
    public string? LocationId { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("uom")]
    public string? Uom { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }
}

public sealed class ProductStatusResponse : EisApiResponse<ProductStatusResponseData>
{
}

/// <summary>Maps EIS <c>TerminalBlockRequest</c> (also used for unblock-status).</summary>
public sealed class CheckTerminalUnblockStatusRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; init; }
}

/// <summary>Maps EIS <c>TerminalUnblockStatusResponse</c>.</summary>
public sealed class TerminalUnblockStatusData
{
    [JsonPropertyName("isUnblocked")]
    public bool IsUnblocked { get; set; }
}

public sealed class CheckTerminalUnblockStatusResponse : EisApiResponse<TerminalUnblockStatusData>
{
}

/// <summary>Maps EIS <c>TinAuthorizationRequirementRequest</c>.</summary>
public sealed class CheckTinAuthorizationRequirementRequest
{
    [JsonPropertyName("tin")]
    public required string Tin { get; init; }
}

/// <summary>Maps EIS <c>TinAuthorizationRequirementResponse</c>.</summary>
public sealed class TinAuthorizationRequirementData
{
    [JsonPropertyName("tin")]
    public string? Tin { get; set; }

    [JsonPropertyName("tinExists")]
    public bool TinExists { get; set; }

    [JsonPropertyName("requiresAuthorizationCode")]
    public bool RequiresAuthorizationCode { get; set; }
}

public sealed class CheckTinAuthorizationRequirementResponse : EisApiResponse<TinAuthorizationRequirementData>
{
}

/// <summary>Maps EIS <c>UnValidatedAuthorizationCode</c>.</summary>
public sealed class ValidateAuthorizationCodeRequest
{
    [JsonPropertyName("authorizationCode")]
    public required string AuthorizationCode { get; init; }
}

/// <summary>Maps EIS <c>ValidatedAuthorizationCode</c>.</summary>
public sealed class ValidatedAuthorizationCodeData
{
    [JsonPropertyName("isValidAuthorizationCode")]
    public bool IsValidAuthorizationCode { get; set; }

    [JsonPropertyName("authorizationCode")]
    public string? AuthorizationCode { get; set; }

    [JsonPropertyName("authorizationReason")]
    public string? AuthorizationReason { get; set; }

    [JsonPropertyName("generatedBy")]
    public string? GeneratedBy { get; set; }

    [JsonPropertyName("generatedOn")]
    public DateTime? GeneratedOn { get; set; }

    [JsonPropertyName("expiresOn")]
    public DateTime? ExpiresOn { get; set; }

    [JsonPropertyName("usedOn")]
    public DateTime? UsedOn { get; set; }
}

public sealed class ValidateAuthorizationCodeResponse : EisApiResponse<ValidatedAuthorizationCodeData>
{
}

/// <summary>
/// Response data for <c>POST /api/v1/configuration/request-new-terminal-token</c>
/// (<c>data</c> is a JWT string).
/// </summary>
public sealed class RequestNewTerminalTokenResponse : EisApiResponse<string>
{
}
