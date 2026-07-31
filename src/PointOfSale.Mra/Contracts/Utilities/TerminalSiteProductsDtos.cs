using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Common;

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
/// Typed EIS envelope for <c>get-terminal-site-products</c>
/// (<c>statusCode</c>, <c>remark</c>, <c>errors</c>, <c>data</c> catalog array).
/// </summary>
public sealed class GetTerminalSiteProductsResponse : EisApiResponse<IReadOnlyList<TerminalSiteProductDto>>
{
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

    /// <summary>
    /// Fiscal line description must match the EIS site catalog exactly.
    /// Prefer <c>description</c> over display <c>productName</c> (MRA rejects mismatches).
    /// </summary>
    public string ResolveName() =>
        NormalizeWhitespace(FirstNonEmpty(Description, ProductName, ProductCode, Barcode)) ?? string.Empty;

    /// <summary>Authoritative EIS description used on sales submit (same as <see cref="ResolveName"/>).</summary>
    public string ResolveFiscalDescription() => ResolveName();

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

    /// <summary>Collapse internal whitespace so EIS exact-match checks do not fail on double spaces.</summary>
    public static string? NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Normalized catalog row ready for Albert Retail Terminal local inventory persistence.
/// </summary>
public sealed class TerminalSiteProductCatalogSnapshot
{
    public required string ProductId { get; init; }
    public required string ProductCode { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal StockQuantity { get; init; }
    public string? UnitOfMeasure { get; init; }
    public string? SiteId { get; init; }
    public DateTime? ProductExpiryDate { get; init; }
    public decimal MinimumStockLevel { get; init; }
    public string? TaxRateId { get; init; }
    public bool IsProduct { get; init; }
    public string? HsCode { get; init; }
}
