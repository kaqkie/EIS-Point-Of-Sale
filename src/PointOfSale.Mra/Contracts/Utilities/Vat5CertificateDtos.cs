using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Common;

namespace PointOfSale.Mra.Contracts.Utilities;

/// <summary>
/// Request body for <c>POST /api/v1/utilities/validate-vat5-certificate</c>.
/// </summary>
public sealed class ValidateVat5CertificateRequest
{
    [JsonPropertyName("projectNumber")]
    public required string ProjectNumber { get; init; }

    [JsonPropertyName("certificateNumber")]
    public required string CertificateNumber { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }
}

/// <summary>
/// Typed EIS envelope for VAT 5 certificate validation.
/// </summary>
public sealed class ValidateVat5CertificateResponse : EisApiResponse<Vat5CertificateValidationData>
{
}

/// <summary>
/// Certificate payload returned in <c>data</c> when validation succeeds.
/// </summary>
public sealed class Vat5CertificateValidationData
{
    [JsonPropertyName("projectNumber")]
    public string? ProjectNumber { get; set; }

    [JsonPropertyName("certificateNumber")]
    public string? CertificateNumber { get; set; }

    /// <summary>Approved certificate quantity (reusable until fully consumed).</summary>
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("dateOfIssue")]
    public DateTime? DateOfIssue { get; set; }

    [JsonPropertyName("dateOfExpiry")]
    public DateTime? DateOfExpiry { get; set; }
}

/// <summary>
/// Local ledger row tracking partial consumption of a reusable VAT5 certificate.
/// </summary>
public sealed class Vat5CertificateBalanceLedger
{
    public required string ProjectNumber { get; init; }
    public required string CertificateNumber { get; init; }
    public decimal ApprovedQuantity { get; set; }
    public decimal ConsumedQuantity { get; set; }
    public DateTime? DateOfIssue { get; set; }
    public DateTime? DateOfExpiry { get; set; }
    public DateTime? LastValidatedUtc { get; set; }
    public DateTime? LastConsumedUtc { get; set; }

    public decimal RemainingQuantity => Math.Max(0m, ApprovedQuantity - ConsumedQuantity);
}
