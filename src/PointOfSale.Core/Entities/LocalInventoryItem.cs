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
}
