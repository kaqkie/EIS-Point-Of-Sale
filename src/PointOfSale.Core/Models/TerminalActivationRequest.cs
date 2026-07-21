namespace PointOfSale.Core.Models;

public sealed class TerminalActivationRequest
{
    public required string TerminalActivationCode { get; init; }
    public string? BranchCode { get; init; }
    public required PlatformEnvironment Platform { get; init; }
    public required PosEnvironment Pos { get; init; }
}

public sealed class PlatformEnvironment
{
    public required string OsName { get; init; }
    public required string OsVersion { get; init; }
    public string? OsBuild { get; init; }
    public required string MacAddress { get; init; }
}

public sealed class PosEnvironment
{
    public required string ProductId { get; init; }
    public required string ProductVersion { get; init; }
}

public sealed class TerminalActivationConfirmationRequest
{
    public required string TerminalId { get; init; }
    public required string TerminalActivationCode { get; init; }
}
