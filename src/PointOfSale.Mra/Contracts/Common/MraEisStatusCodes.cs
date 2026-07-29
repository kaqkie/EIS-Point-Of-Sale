namespace PointOfSale.Mra.Contracts.Common;

/// <summary>
/// Well-known MRA EIS logical <c>statusCode</c> and field <c>errorCode</c> values.
/// Success remains <c>statusCode == 1</c> on <see cref="EisApiResponse{T}"/>.
/// </summary>
public static class MraEisStatusCodes
{
    /// <summary>Server / infrastructure fault (DB or backend unreachable).</summary>
    public const int ServerError = -100500;

    /// <summary>Authentication failure — refresh JWT / credentials.</summary>
    public const int AuthenticationFailure = -100401;

    /// <summary>Business rule violation (e.g. sale before terminal activation).</summary>
    public const int BusinessRuleViolation = -100999;

    /// <summary>Outdated configuration — run <c>get-latest-configs</c>.</summary>
    public const int OutdatedConfiguration = -100000;

    /// <summary>Terminal has been de-activated — re-activate before selling.</summary>
    public const int TerminalDeactivated = -199999;

    /// <summary>Mandatory field missing from the request payload.</summary>
    public const int MissingMandatoryField = -200010;

    /// <summary>Field value out of range, wrong length, or pattern mismatch.</summary>
    public const int InvalidFieldValue = -200011;
}
