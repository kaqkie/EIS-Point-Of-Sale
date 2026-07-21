namespace PointOfSale.Core.Entities;

public sealed class LocalInventoryItem
{
    public required string ProductId { get; set; }
    public required string ProductCode { get; set; }
    public required string Name { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal StockQuantity { get; set; }
    public string? HsCode { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? TaxRateId { get; set; }

    /// <summary>Local | HeadOffice — catalog ownership for conflict resolution.</summary>
    public string CatalogSource { get; set; } = "Local";

    public DateTime? HeadOfficeRevisionUtc { get; set; }
    public DateTime? LastReplicatedAtUtc { get; set; }
}
