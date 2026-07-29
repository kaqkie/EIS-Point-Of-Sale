namespace PointOfSale.Mra.Contracts.Common;

/// <summary>High-level classification of an MRA EIS failure.</summary>
public enum MraEisFailureCategory
{
    None = 0,
    ServerError,
    AuthenticationFailure,
    BusinessRuleViolation,
    OutdatedConfiguration,
    TerminalDeactivated,
    MissingMandatoryField,
    InvalidFieldValue,
    Unknown
}

/// <summary>Recommended terminal action after evaluating an MRA EIS response.</summary>
public enum MraEisRecommendedAction
{
    None = 0,

    /// <summary>Keep the invoice in the offline queue and retry later.</summary>
    RetryLater,

    /// <summary>Renew / re-fetch the terminal JWT (or full credentials) then retry.</summary>
    RefreshCredentials,

    /// <summary>Run get-latest-configs, then retry the operation.</summary>
    SyncLatestConfigs,

    /// <summary>Guide the operator through terminal (re)activation.</summary>
    ReactivateTerminal,

    /// <summary>Quarantine the queued invoice — payload must be corrected manually.</summary>
    QuarantinePayload,

    /// <summary>Block selling until business prerequisites are met (activation, config).</summary>
    BlockUntilReady
}

/// <summary>
/// Structured outcome of parsing an MRA EIS <c>statusCode</c> / <c>errors[]</c> envelope.
/// </summary>
public sealed class MraEisResponseEvaluation
{
    public bool IsSuccess { get; init; }

    public int StatusCode { get; init; }

    public string? Remark { get; init; }

    public MraEisFailureCategory Category { get; init; }

    public MraEisRecommendedAction RecommendedAction { get; init; }

    /// <summary>Primary MRA status or field error code that drove the classification.</summary>
    public int PrimaryCode { get; init; }

    public IReadOnlyList<EisApiError> Errors { get; init; } = Array.Empty<EisApiError>();

    /// <summary>Short title suitable for MessageBox / toast headers.</summary>
    public string OperatorTitle { get; init; } = string.Empty;

    /// <summary>Operator-facing guidance (what happened + what to do next).</summary>
    public string OperatorMessage { get; init; } = string.Empty;

    /// <summary>Compact detail for queue / audit logs (includes field-level errors).</summary>
    public string TechnicalDetail { get; init; } = string.Empty;

    /// <summary>True when the offline sync queue should quarantine instead of retrying.</summary>
    public bool ShouldQuarantine =>
        RecommendedAction is MraEisRecommendedAction.QuarantinePayload
            or MraEisRecommendedAction.BlockUntilReady
            or MraEisRecommendedAction.ReactivateTerminal;

    /// <summary>True when a later automatic retry may succeed without payload edits.</summary>
    public bool IsTransient =>
        RecommendedAction is MraEisRecommendedAction.RetryLater
            or MraEisRecommendedAction.RefreshCredentials
            or MraEisRecommendedAction.SyncLatestConfigs;

    /// <summary>Suggest completing the sale offline when the network/backend is down.</summary>
    public bool SuggestOfflineFallback =>
        Category is MraEisFailureCategory.ServerError
            or MraEisFailureCategory.AuthenticationFailure;

    public static MraEisResponseEvaluation Success(string? remark = null) =>
        new()
        {
            IsSuccess = true,
            StatusCode = 1,
            Remark = remark,
            Category = MraEisFailureCategory.None,
            RecommendedAction = MraEisRecommendedAction.None,
            PrimaryCode = 1,
            OperatorTitle = "MRA OK",
            OperatorMessage = remark ?? "MRA EIS accepted the request.",
            TechnicalDetail = remark ?? "statusCode=1"
        };
}
