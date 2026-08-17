using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Offline threshold checks (transaction age + cumulative amount) per MRA terminal OfflineLimit.
/// </summary>
public interface IOfflineTransactionComplianceValidator
{
    OfflineTransactionComplianceResult ValidateForUpload(
        SubmitSalesTransactionRequest request,
        OfflineLimitDto? offlineLimit,
        DateTime queuedAtUtc,
        DateTime? asOfUtc = null,
        decimal pendingOfflineCumulativeAmount = 0m);

    /// <summary>
    /// Gate for starting/continuing offline sales: wall-clock time since last MRA contact,
    /// age of existing pending work, and cumulative amount including the prospective sale
    /// must stay within terminal OfflineLimit (default 72h; updates when MRA changes it).
    /// </summary>
    OfflineTransactionComplianceResult ValidateCanContinueOffline(
        decimal prospectiveInvoiceTotal,
        OfflineLimitDto? offlineLimit,
        decimal pendingOfflineCumulativeAmount,
        DateTime? oldestPendingQueuedAtUtc,
        DateTime? asOfUtc = null,
        DateTime? lastMraReachableUtc = null);
}

public sealed class OfflineTransactionComplianceValidator : IOfflineTransactionComplianceValidator
{
    public const int FallbackMaxTransactionAgeInHours = 72;

    public OfflineTransactionComplianceResult ValidateForUpload(
        SubmitSalesTransactionRequest request,
        OfflineLimitDto? offlineLimit,
        DateTime queuedAtUtc,
        DateTime? asOfUtc = null,
        decimal pendingOfflineCumulativeAmount = 0m)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = (asOfUtc ?? DateTime.UtcNow).ToUniversalTime();
        var invoiceMoment = NormalizeUtc(request.InvoiceHeader.InvoiceDateTime);
        var queuedMoment = NormalizeUtc(queuedAtUtc);

        // Age is measured from the earlier of invoice time vs queue insert (conservative).
        var transactionMoment = invoiceMoment <= queuedMoment ? invoiceMoment : queuedMoment;
        var maxAgeHours = ResolveMaxAgeHours(offlineLimit);
        var maxAge = TimeSpan.FromHours((double)maxAgeHours);
        var age = now - transactionMoment;

        if (age > maxAge)
        {
            return OfflineTransactionComplianceResult.Reject(
                $"Offline transaction exceeded allowed age ({age.TotalHours:0.#}h > {maxAgeHours}h). " +
                "Reconnect to the MRA server and sync before continuing offline sales.");
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceSummary.OfflineSignature))
        {
            return OfflineTransactionComplianceResult.Reject(
                "Offline queue item is missing invoiceSummary.offlineSignature. " +
                "MRA requires the HMAC offline signature for cryptographic validation on upload.");
        }

        var amountCheck = ValidateCumulativeAmount(
            request.InvoiceSummary.InvoiceTotal,
            offlineLimit,
            pendingOfflineCumulativeAmount);
        if (amountCheck is not null)
        {
            return amountCheck;
        }

        return OfflineTransactionComplianceResult.Ok(
            (int)Math.Ceiling(maxAgeHours),
            age,
            request.InvoiceSummary.OfflineSignature);
    }

    public OfflineTransactionComplianceResult ValidateCanContinueOffline(
        decimal prospectiveInvoiceTotal,
        OfflineLimitDto? offlineLimit,
        decimal pendingOfflineCumulativeAmount,
        DateTime? oldestPendingQueuedAtUtc,
        DateTime? asOfUtc = null,
        DateTime? lastMraReachableUtc = null)
    {
        var now = (asOfUtc ?? DateTime.UtcNow).ToUniversalTime();
        var maxAgeHours = ResolveMaxAgeHours(offlineLimit);
        var maxAge = TimeSpan.FromHours((double)maxAgeHours);

        // Wall-clock offline window since last successful MRA contact (MRA OfflineLimit).
        if (lastMraReachableUtc is DateTime lastReachable)
        {
            var lastReachableUtc = NormalizeUtc(lastReachable);
            var offlineDuration = now - lastReachableUtc;
            if (offlineDuration > maxAge)
            {
                return OfflineTransactionComplianceResult.Reject(
                    $"This terminal has been offline from MRA for {offlineDuration.TotalHours:0.#}h, " +
                    $"which exceeds the allowed offline window of {maxAgeHours}h. " +
                    "Reconnect to the MRA server before continuing sales.");
            }
        }

        if (oldestPendingQueuedAtUtc is DateTime oldest)
        {
            var oldestUtc = NormalizeUtc(oldest);
            var pendingAge = now - oldestUtc;
            if (pendingAge > maxAge)
            {
                return OfflineTransactionComplianceResult.Reject(
                    $"Offline queue already exceeds allowed age ({pendingAge.TotalHours:0.#}h > {maxAgeHours}h). " +
                    "Reconnect to the MRA server and sync pending sales before taking more offline transactions.");
            }
        }

        var amountCheck = ValidateCumulativeAmount(
            prospectiveInvoiceTotal,
            offlineLimit,
            pendingOfflineCumulativeAmount);
        if (amountCheck is not null)
        {
            return amountCheck;
        }

        return OfflineTransactionComplianceResult.Ok(
            (int)Math.Ceiling(maxAgeHours),
            age: lastMraReachableUtc is DateTime lr
                ? now - NormalizeUtc(lr)
                : TimeSpan.Zero,
            offlineSignature: string.Empty);
    }

    /// <summary>Resolves MRA OfflineLimit.maxTransactionAgeInHours, falling back to 72.</summary>
    public static decimal ResolveMaxAgeHours(OfflineLimitDto? offlineLimit) =>
        offlineLimit?.MaxTransactionAgeInHours > 0
            ? offlineLimit.MaxTransactionAgeInHours
            : FallbackMaxTransactionAgeInHours;

    private static OfflineTransactionComplianceResult? ValidateCumulativeAmount(
        decimal prospectiveInvoiceTotal,
        OfflineLimitDto? offlineLimit,
        decimal pendingOfflineCumulativeAmount)
    {
        if (offlineLimit?.MaxCummulativeAmount is not > 0)
        {
            return null;
        }

        var limit = offlineLimit.MaxCummulativeAmount;
        var pending = Math.Max(0m, pendingOfflineCumulativeAmount);
        var projected = pending + Math.Max(0m, prospectiveInvoiceTotal);

        if (prospectiveInvoiceTotal > limit)
        {
            return OfflineTransactionComplianceResult.Reject(
                $"Invoice total {prospectiveInvoiceTotal:0.##} exceeds this terminal's offline amount limit " +
                $"{limit:0.##}. Connect to the MRA server to continue.");
        }

        if (projected > limit)
        {
            return OfflineTransactionComplianceResult.Reject(
                $"Offline cumulative amount {projected:0.##} (pending {pending:0.##} + this sale {prospectiveInvoiceTotal:0.##}) " +
                $"would exceed the terminal offline limit {limit:0.##}. " +
                "Reconnect and sync with MRA before continuing offline.");
        }

        return null;
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}

public sealed class OfflineTransactionComplianceResult
{
    public bool IsCompliant { get; init; }
    public bool ShouldQuarantine { get; init; }
    public string? Remark { get; init; }
    public int MaxAgeHours { get; init; }
    public TimeSpan? Age { get; init; }
    public string? OfflineSignatureFingerprint { get; init; }

    public static OfflineTransactionComplianceResult Ok(
        int maxAgeHours,
        TimeSpan age,
        string offlineSignature) =>
        new()
        {
            IsCompliant = true,
            ShouldQuarantine = false,
            MaxAgeHours = maxAgeHours,
            Age = age,
            OfflineSignatureFingerprint = TruncateSig(offlineSignature),
            Remark = "Offline transaction is within age limit and carries offlineSignature."
        };

    public static OfflineTransactionComplianceResult Reject(string remark) =>
        new()
        {
            IsCompliant = false,
            ShouldQuarantine = true,
            Remark = remark
        };

    /// <summary>Backward-compatible alias for <see cref="Reject"/>.</summary>
    public static OfflineTransactionComplianceResult Quarantine(string remark) => Reject(remark);

    private static string TruncateSig(string signature) =>
        string.IsNullOrEmpty(signature)
            ? string.Empty
            : signature.Length <= 16
                ? signature
                : signature[..16] + "…";
}
