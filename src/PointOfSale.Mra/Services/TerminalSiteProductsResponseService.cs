using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Mra.Services;

public interface ITerminalSiteProductsResponseService
{
    /// <summary>Deserializes a raw EIS JSON body into the typed site-products envelope.</summary>
    TerminalSiteProductsParseResult ParseJson(string? json);

    /// <summary>Validates an already-deserialized EIS envelope.</summary>
    TerminalSiteProductsParseResult Validate(EisApiResponse<IReadOnlyList<TerminalSiteProductDto>>? response);

    /// <summary>
    /// Maps valid catalog rows into normalized snapshots for local inventory persistence.
    /// Rows missing <c>productCode</c>/<c>barcode</c> are skipped.
    /// </summary>
    IReadOnlyList<TerminalSiteProductCatalogSnapshot> BuildCatalogSnapshots(
        IEnumerable<TerminalSiteProductDto> products);
}

/// <summary>
/// Parses and validates MRA EIS <c>get-terminal-site-products</c> responses for
/// Albert Retail Terminal catalog caching and POS lookup.
/// </summary>
public sealed class TerminalSiteProductsResponseService : ITerminalSiteProductsResponseService
{
    private readonly ILogger<TerminalSiteProductsResponseService> _logger;

    public TerminalSiteProductsResponseService(ILogger<TerminalSiteProductsResponseService> logger)
    {
        _logger = logger;
    }

    public TerminalSiteProductsParseResult ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return TerminalSiteProductsParseResult.Failed("Empty MRA response body.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<GetTerminalSiteProductsResponse>(
                json,
                MraJson.SerializerOptions);
            return Validate(response);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize get-terminal-site-products JSON.");
            return TerminalSiteProductsParseResult.Failed(
                "MRA get-terminal-site-products response was not valid JSON.",
                ex.Message);
        }
    }

    public TerminalSiteProductsParseResult Validate(EisApiResponse<IReadOnlyList<TerminalSiteProductDto>>? response)
    {
        if (response is null)
        {
            return TerminalSiteProductsParseResult.Failed("MRA response deserialized to null.");
        }

        if (!response.IsSuccess)
        {
            var errorDetail = FormatErrors(response.Errors);
            _logger.LogWarning(
                "get-terminal-site-products logical failure. statusCode={StatusCode} remark={Remark} errors={Errors}",
                response.StatusCode,
                response.Remark ?? "(null)",
                errorDetail);
            return TerminalSiteProductsParseResult.Failed(
                response.Remark ?? $"MRA returned statusCode {response.StatusCode}.",
                errorDetail,
                response.StatusCode,
                response.Errors);
        }

        var catalog = response.Data ?? Array.Empty<TerminalSiteProductDto>();
        var snapshots = BuildCatalogSnapshots(catalog);
        var skipped = catalog.Count - snapshots.Count;

        _logger.LogInformation(
            "Parsed get-terminal-site-products catalog items={Items} usable={Usable} skipped={Skipped}",
            catalog.Count,
            snapshots.Count,
            skipped);

        return TerminalSiteProductsParseResult.Succeeded(
            catalog,
            snapshots,
            response.Remark,
            response.StatusCode,
            skipped);
    }

    public IReadOnlyList<TerminalSiteProductCatalogSnapshot> BuildCatalogSnapshots(
        IEnumerable<TerminalSiteProductDto> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        var snapshots = new List<TerminalSiteProductCatalogSnapshot>();
        foreach (var product in products)
        {
            var code = product.ResolveProductCode();
            if (string.IsNullOrWhiteSpace(code))
            {
                _logger.LogWarning("Skipping MRA site product with missing productCode/barcode.");
                continue;
            }

            var name = product.ResolveName();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = code;
            }

            snapshots.Add(new TerminalSiteProductCatalogSnapshot
            {
                ProductId = code,
                ProductCode = code,
                Name = name,
                Description = string.IsNullOrWhiteSpace(product.Description) ? null : product.Description.Trim(),
                UnitPrice = product.Price,
                StockQuantity = product.Quantity,
                UnitOfMeasure = string.IsNullOrWhiteSpace(product.UnitOfMeasure) ? null : product.UnitOfMeasure.Trim(),
                SiteId = string.IsNullOrWhiteSpace(product.SiteId) ? null : product.SiteId.Trim(),
                ProductExpiryDate = product.ProductExpiryDate,
                MinimumStockLevel = product.MinimumStockLevel,
                TaxRateId = string.IsNullOrWhiteSpace(product.TaxRateId) ? null : product.TaxRateId.Trim(),
                IsProduct = product.IsProduct,
                HsCode = string.IsNullOrWhiteSpace(product.HsCode) ? null : product.HsCode.Trim()
            });
        }

        return snapshots;
    }

    private static string FormatErrors(IReadOnlyList<EisApiError>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            "; ",
            errors.Take(8).Select(e =>
                string.IsNullOrWhiteSpace(e.FieldName)
                    ? $"[{e.ErrorCode}] {e.ErrorMessage}"
                    : $"[{e.ErrorCode}] {e.FieldName}: {e.ErrorMessage}"));
    }
}

public sealed class TerminalSiteProductsParseResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<TerminalSiteProductDto> Products { get; init; } = Array.Empty<TerminalSiteProductDto>();
    public IReadOnlyList<TerminalSiteProductCatalogSnapshot> Snapshots { get; init; } =
        Array.Empty<TerminalSiteProductCatalogSnapshot>();
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public int SkippedInvalidRows { get; init; }
    public int ProductCount => Products.Count;
    public int UsableCount => Snapshots.Count;
    public int ServiceCount => Snapshots.Count(s => !s.IsProduct);

    public static TerminalSiteProductsParseResult Succeeded(
        IReadOnlyList<TerminalSiteProductDto> products,
        IReadOnlyList<TerminalSiteProductCatalogSnapshot> snapshots,
        string? remark,
        int statusCode,
        int skippedInvalidRows) =>
        new()
        {
            Success = true,
            StatusCode = statusCode,
            Remark = remark,
            Products = products,
            Snapshots = snapshots,
            SkippedInvalidRows = skippedInvalidRows
        };

    public static TerminalSiteProductsParseResult Failed(
        string remark,
        string? errorDetail = null,
        int statusCode = 0,
        IReadOnlyList<EisApiError>? errors = null) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            Remark = remark,
            ErrorDetail = errorDetail,
            Errors = errors
        };
}
