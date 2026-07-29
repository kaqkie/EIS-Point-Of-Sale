using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Utilities;

/// <summary>
/// Request body for <c>POST /api/v1/utilities/get-terminal-site-products</c>.
/// </summary>
public sealed class GetTerminalSiteProductsRequest
{
    [JsonPropertyName("tin")]
    public required string Tin { get; init; }

    [JsonPropertyName("siteId")]
    public required string SiteId { get; init; }
}

/// <summary>
/// Product/service row returned in the EIS <c>data</c> array for terminal site catalog sync.
/// </summary>
public sealed class TerminalSiteProductDto
{
    [JsonPropertyName("productCode")]
    public string? ProductCode { get; set; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitOfMeasure")]
    public string? UnitOfMeasure { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("siteId")]
    public string? SiteId { get; set; }

    [JsonPropertyName("productExpiryDate")]
    public DateTime? ProductExpiryDate { get; set; }

    [JsonPropertyName("minimumStockLevel")]
    public decimal MinimumStockLevel { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }

    [JsonPropertyName("isProduct")]
    public bool IsProduct { get; set; } = true;

    [JsonPropertyName("hsCode")]
    public string? HsCode { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    public string ResolveProductCode() =>
        FirstNonEmpty(ProductCode, Barcode) ?? string.Empty;

    public string ResolveName() =>
        FirstNonEmpty(ProductName, Description, ProductCode, Barcode) ?? string.Empty;

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
