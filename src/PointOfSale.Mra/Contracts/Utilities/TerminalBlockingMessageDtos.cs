using System.Text.Json.Serialization;
using PointOfSale.Mra.Contracts.Common;

namespace PointOfSale.Mra.Contracts.Utilities;

/// <summary>
/// Request body for <c>POST /api/v1/utilities/get-terminal-blocking-message</c>.
/// </summary>
public sealed class GetTerminalBlockingMessageRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; init; }
}

/// <summary>
/// Typed EIS envelope for terminal blocking message retrieval.
/// </summary>
public sealed class GetTerminalBlockingMessageResponse : EisApiResponse<TerminalBlockingMessageData>
{
}

/// <summary>
/// Official MRA blocking explanation returned in <c>data</c>.
/// </summary>
public sealed class TerminalBlockingMessageData
{
    [JsonPropertyName("isBlocked")]
    public bool IsBlocked { get; set; }

    [JsonPropertyName("blockingReason")]
    public string? BlockingReason { get; set; }

    [JsonPropertyName("blockedAt")]
    public DateTime? BlockedAt { get; set; }

    /// <summary>Operator-facing explanation (blocking reason, trimmed).</summary>
    public string ResolveOperatorMessage() =>
        string.IsNullOrWhiteSpace(BlockingReason)
            ? "This terminal has been blocked by the Malawi Revenue Authority. Sales processing must stop until the terminal is unblocked."
            : BlockingReason.Trim();
}
