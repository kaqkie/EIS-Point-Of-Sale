namespace PointOfSale.Core.Entities;

public sealed class CashierShift
{
    public int ShiftId { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public required string CashierName { get; set; }
    public decimal OpeningFloat { get; set; }
    public decimal? ClosingCashCounted { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CashVariance { get; set; }
    public required string Status { get; set; }
    public string? ZReportJson { get; set; }
    public string? Notes { get; set; }
}

public sealed class ShiftCashMovement
{
    public int MovementId { get; set; }
    public int ShiftId { get; set; }
    public required string MovementType { get; set; }
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public static class ShiftStatuses
{
    public const string Open = "Open";
    public const string Closed = "Closed";
}

public static class ShiftCashMovementTypes
{
    public const string CashIn = "CashIn";
    public const string CashOut = "CashOut";
}
