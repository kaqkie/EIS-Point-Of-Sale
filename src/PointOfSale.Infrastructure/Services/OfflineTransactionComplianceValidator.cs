using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Pre-upload compliance checks for offline queue items (transaction age + offlineSignature).
/// </summary>
public interface IOfflineTransactionComplianceValidator
{
    OfflineTransactionComplianceResult ValidateForUpload(
        SubmitSalesTransactionRequest request,
        OfflineLimitDto? offlineLimit,
        DateTime queuedAtUtc,
        DateTime? asOfUtc = null);
}

public sealed class OfflineTransactionComplianceValidator : IOfflineTransactionComplianceValidator
{
    public const int FallbackMaxTransactionAgeInHours = 72;

    public OfflineTransactionComplianceResult ValidateForUpload(
        SubmitSalesTransactionRequest request,
        OfflineLimitDto? offlineLimit,
        DateTime queuedAtUtc,
        DateTime? asOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = (asOfUtc ?? DateTime.UtcNow).ToUniversalTime();
        var invoiceMoment = NormalizeUtc(request.InvoiceHeader.InvoiceDateTime);
        var queuedMoment = NormalizeUtc(queuedAtUtc);

        // Age is measured from the earlier of invoice time vs queue insert (conservative).
        var transactionMoment = invoiceMoment <= queuedMoment ? invoiceMoment : queuedMoment;
        var maxAgeHours = offlineLimit?.MaxTransactionAgeInHours > 0
            ? offlineLimit.MaxTransactionAgeInHours
            : FallbackMaxTransactionAgeInHours;
        var maxAge = TimeSpan.FromHours(maxAgeHours);
        var age = now - transactionMoment;

        if (age > maxAge)
        {
            return OfflineTransactionComplianceResult.Quarantine(
                $"Offline transaction exceeded allowed age ({age.TotalHours:0.#}h > {maxAgeHours}h). " +
                "MRA will reject over-age offline sales — contact supervisor / MRA before retry.");
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceSummary.OfflineSignature))
        {
            return OfflineTransactionComplianceResult.Quarantine(
                "Offline queue item is missing invoiceSummary.offlineSignature. " +
                "MRA requires the HMAC offline signature for cryptographic validation on upload.");
        }

        if (offlineLimit?.MaxCummulativeAmount > 0
            && request.InvoiceSummary.InvoiceTotal > offlineLimit.MaxCummulativeAmount)
        {
            return OfflineTransactionComplianceResult.Quarantine(
                $"Invoice total {request.InvoiceSummary.InvoiceTotal:0.##} exceeds terminal offline cumulative limit " +
                $"{offlineLimit.MaxCummulativeAmount:0.##}.");
        }

        return OfflineTransactionComplianceResult.Ok(
            maxAgeHours,
            age,
            request.InvoiceSummary.OfflineSignature);
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

    public static OfflineTransactionComplianceResult Quarantine(string remark) =>
        new()
        {
            IsCompliant = false,
            ShouldQuarantine = true,
            Remark = remark
        };

    private static string TruncateSig(string signature) =>
        signature.Length <= 16 ? signature : signature[..16] + "…";
}
