using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Common;

namespace PointOfSale.Mra.Contracts.Utilities;

/// <summary>
/// Request body for <c>POST /api/v1/utilities/validate-vat5-certificate</c>
/// (EIS <c>Vat5CertificateValidationRequest</c>).
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
/// Maps EIS <c>Vat5CertificateValidationResponse</c>.
/// </summary>
public sealed class Vat5CertificateValidationData
{
    [JsonPropertyName("projectNumber")]
    public string? ProjectNumber { get; set; }

    [JsonPropertyName("vat5CertificateNumber")]
    public string? Vat5CertificateNumber { get; set; }

    /// <summary>Legacy alias accepted when older payloads use <c>certificateNumber</c>.</summary>
    [JsonPropertyName("certificateNumber")]
    public string? CertificateNumber { get; set; }

    /// <summary>Approved certificate quantity (reusable until fully consumed).</summary>
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("dateOfIssue")]
    public DateTime? DateOfIssue { get; set; }

    [JsonPropertyName("dateOfExpiry")]
    public DateTime? DateOfExpiry { get; set; }

    [JsonPropertyName("isValid")]
    public bool? IsValid { get; set; }

    public string? ResolveCertificateNumber() =>
        FirstNonEmpty(Vat5CertificateNumber, CertificateNumber);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
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
