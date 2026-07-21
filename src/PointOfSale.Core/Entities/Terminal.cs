namespace PointOfSale.Core.Entities;

public sealed class Terminal
{
    public required string TerminalId { get; set; }
    public string? BranchCode { get; set; }
    public required string ActivationState { get; set; }
    public string? SecretKey { get; set; }
    public DateTime? LastSyncedAt { get; set; }
}
