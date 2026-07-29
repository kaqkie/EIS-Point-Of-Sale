using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Sales;

/// <summary>Maps EIS <c>SalesInvoice</c>.</summary>
public sealed record SubmitSalesTransactionRequest
{
    [JsonPropertyName("invoiceHeader")]
    public required InvoiceHeaderDto InvoiceHeader { get; init; }

    [JsonPropertyName("invoiceLineItems")]
    public required IReadOnlyList<InvoiceLineItemDto> InvoiceLineItems { get; init; }

    [JsonPropertyName("invoiceSummary")]
    public required InvoiceSummaryDto InvoiceSummary { get; init; }
}

/// <summary>Maps EIS <c>InvoiceHeader</c>.</summary>
public sealed class InvoiceHeaderDto
{
    [JsonPropertyName("invoiceNumber")]
    public required string InvoiceNumber { get; init; }

    [JsonPropertyName("invoiceDateTime")]
    public DateTime InvoiceDateTime { get; init; }

    [JsonPropertyName("sellerTIN")]
    public required string SellerTin { get; init; }

    [JsonPropertyName("buyerTIN")]
    public string? BuyerTin { get; init; }

    [JsonPropertyName("buyerName")]
    public string? BuyerName { get; init; }

    [JsonPropertyName("buyerAuthorizationCode")]
    public string? BuyerAuthorizationCode { get; init; }

    [JsonPropertyName("siteId")]
    public required string SiteId { get; init; }

    [JsonPropertyName("globalConfigVersion")]
    public int GlobalConfigVersion { get; init; }

    [JsonPropertyName("taxpayerConfigVersion")]
    public int TaxpayerConfigVersion { get; init; }

    [JsonPropertyName("terminalConfigVersion")]
    public int TerminalConfigVersion { get; init; }

    [JsonPropertyName("isExport")]
    public bool IsExport { get; init; }

    [JsonPropertyName("isReliefSupply")]
    public bool IsReliefSupply { get; init; }

    [JsonPropertyName("vat5CertificateDetails")]
    public Vat5CertificateDetailsDto? Vat5CertificateDetails { get; init; }

    [JsonPropertyName("paymentMethod")]
    public required string PaymentMethod { get; init; }
}

/// <summary>Maps EIS <c>Vat5CertificateDto</c>.</summary>
public sealed class Vat5CertificateDetailsDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("projectNumber")]
    public string? ProjectNumber { get; init; }

    [JsonPropertyName("certificateNumber")]
    public string? CertificateNumber { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }
}

/// <summary>Maps EIS <c>LineItemDto</c>.</summary>
public sealed class InvoiceLineItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("discount")]
    public decimal Discount { get; init; }

    [JsonPropertyName("total")]
    public decimal Total { get; init; }

    [JsonPropertyName("totalVAT")]
    public decimal TotalVat { get; init; }

    [JsonPropertyName("taxRateId")]
    public required string TaxRateId { get; init; }

    [JsonPropertyName("isProduct")]
    public bool IsProduct { get; init; } = true;
}

/// <summary>Maps EIS <c>InvoiceSummary</c>.</summary>
public sealed record InvoiceSummaryDto
{
    [JsonPropertyName("taxBreakDown")]
    public required IReadOnlyList<TaxBreakDownDto> TaxBreakDown { get; init; }

    [JsonPropertyName("levyBreakDown")]
    public IReadOnlyList<LevyBreakDownDto>? LevyBreakDown { get; init; }

    [JsonPropertyName("totalVAT")]
    public decimal TotalVat { get; init; }

    [JsonPropertyName("offlineSignature")]
    public string? OfflineSignature { get; init; }

    [JsonPropertyName("invoiceTotal")]
    public decimal InvoiceTotal { get; init; }

    /// <summary>Cash/card amount tendered by the buyer (mandatory for EIS sales submit).</summary>
    [JsonPropertyName("amountTendered")]
    public decimal AmountTendered { get; init; }
}

/// <summary>Maps EIS <c>TaxBreakDown</c>.</summary>
public sealed class TaxBreakDownDto
{
    [JsonPropertyName("rateId")]
    public required string RateId { get; init; }

    [JsonPropertyName("taxableAmount")]
    public decimal TaxableAmount { get; init; }

    [JsonPropertyName("taxAmount")]
    public decimal TaxAmount { get; init; }
}

/// <summary>Maps EIS <c>LevyBreakDown</c>.</summary>
public sealed class LevyBreakDownDto
{
    [JsonPropertyName("levyTypeId")]
    public required string LevyTypeId { get; init; }

    [JsonPropertyName("levyRate")]
    public decimal LevyRate { get; init; }

    [JsonPropertyName("levyAmount")]
    public decimal LevyAmount { get; init; }
}

/// <summary>
/// Maps EIS <c>InvoiceResponse</c> from submit-sales-transaction.
/// Local enrichment fields (<see cref="InvoiceNumber"/>, fiscal tokens) are used for offline queue / receipt printing.
/// </summary>
public sealed class SubmitSalesTransactionResponseData
{
    [JsonPropertyName("validationURL")]
    public string? ValidationUrl { get; set; }

    [JsonPropertyName("shouldDownloadLatestConfig")]
    public bool ShouldDownloadLatestConfig { get; set; }

    /// <summary>
    /// When true, Albert Retail Terminal must call <c>get-terminal-blocking-message</c>,
    /// display the official reason, and stop further sales processing.
    /// </summary>
    [JsonPropertyName("shouldBlockTerminal")]
    public bool ShouldBlockTerminal { get; set; }

    [JsonPropertyName("validationErrors")]
    public IReadOnlyList<string>? ValidationErrors { get; set; }

    /// <summary>
    /// Alternate EIS flag used by some responses; treated the same as <see cref="ShouldBlockTerminal"/>.
    /// </summary>
    [JsonPropertyName("shouldBoardTerminal")]
    public bool ShouldBoardTerminal { get; set; }

    /// <summary>True when either boarding/blocking flag requires the blocking-message utility call.</summary>
    [JsonIgnore]
    public bool RequiresTerminalBlockHandling => ShouldBlockTerminal || ShouldBoardTerminal;

    // --- Local / offline enrichment (also accepts legacy cached JSON) ---

    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("fiscalCode")]
    public string? FiscalCode { get; set; }

    [JsonPropertyName("fiscalSignature")]
    public string? FiscalSignature { get; set; }

    /// <summary>Legacy alias; prefer <see cref="ValidationUrl"/> for live EIS responses.</summary>
    [JsonPropertyName("verificationUrl")]
    public string? VerificationUrl { get; set; }

    public string? ResolveVerificationUrl() =>
        FirstNonEmpty(ValidationUrl, VerificationUrl);

    public string ResolveFiscalSignature() =>
        !string.IsNullOrWhiteSpace(FiscalSignature) ? FiscalSignature! : FiscalCode ?? string.Empty;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

/// <summary>Maps EIS <c>InvoiceLookupRequest</c>.</summary>
public sealed class InvoiceNumberQueryRequest
{
    [JsonPropertyName("invoiceNumber")]
    public required string InvoiceNumber { get; init; }
}

/// <summary>Maps EIS <c>InvoiceLookupResponse</c>.</summary>
public sealed class InvoiceLookupResponseData
{
    [JsonPropertyName("invoiceHeader")]
    public InvoiceHeaderDto? InvoiceHeader { get; set; }

    [JsonPropertyName("invoiceLineItems")]
    public IReadOnlyList<InvoiceLineItemDto>? InvoiceLineItems { get; set; }

    [JsonPropertyName("invoiceSummary")]
    public InvoiceSummaryDto? InvoiceSummary { get; set; }

    [JsonPropertyName("dateSubmitted")]
    public DateTime? DateSubmitted { get; set; }

    [JsonPropertyName("validationURL")]
    public string? ValidationUrl { get; set; }
}

/// <summary>Maps EIS <c>InvoiceAdjustmentRequest</c> (credit/debit note).</summary>
public sealed class ProcessCreditDebitNoteRequest
{
    [JsonPropertyName("invoiceHeader")]
    public required InvoiceHeaderDto InvoiceHeader { get; init; }

    [JsonPropertyName("invoiceLineItems")]
    public required IReadOnlyList<InvoiceLineItemDto> InvoiceLineItems { get; init; }

    [JsonPropertyName("invoiceSummary")]
    public required InvoiceSummaryDto InvoiceSummary { get; init; }

    [JsonPropertyName("reasonForAdjustment")]
    public string? ReasonForAdjustment { get; init; }
}

/// <summary>Maps EIS <c>InvoiceAdjustmentResponse</c>.</summary>
public sealed class ProcessCreditDebitNoteResponseData
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("originalInvoiceNumber")]
    public string? OriginalInvoiceNumber { get; set; }

    [JsonPropertyName("noteType")]
    public string? NoteType { get; set; }

    [JsonPropertyName("validationUrl")]
    public string? ValidationUrl { get; set; }

    [JsonPropertyName("invoiceTotal")]
    public decimal InvoiceTotal { get; set; }

    [JsonPropertyName("totalVat")]
    public decimal TotalVat { get; set; }

    [JsonPropertyName("lineItems")]
    public IReadOnlyList<InvoiceLineItemDto>? LineItems { get; set; }

    [JsonPropertyName("serviceLineItems")]
    public IReadOnlyList<AdjustmentServiceLineItemDto>? ServiceLineItems { get; set; }
}

/// <summary>Maps EIS <c>AdjustmentServiceLineItemDto</c>.</summary>
public sealed class AdjustmentServiceLineItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("productCode")]
    public string? ProductCode { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("discount")]
    public decimal Discount { get; set; }

    [JsonPropertyName("total")]
    public decimal Total { get; set; }

    [JsonPropertyName("totalVAT")]
    public decimal TotalVat { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }

    [JsonPropertyName("isProduct")]
    public bool IsProduct { get; set; }
}

/// <summary>Maps EIS <c>VoidReceiptCreateDto</c>.</summary>
public sealed class CancelReceiptRequest
{
    [JsonPropertyName("receiptNumber")]
    public required string ReceiptNumber { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("supportingDocuments")]
    public string? SupportingDocuments { get; init; }
}

/// <summary>Maps EIS <c>VoidReceiptResponseDto</c> (create void response).</summary>
public sealed class CancelReceiptResponseData
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("requestReason")]
    public string? RequestReason { get; set; }

    [JsonPropertyName("issueDate")]
    public DateOnly? IssueDate { get; set; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; set; }

    [JsonPropertyName("requestedOn")]
    public DateTime? RequestedOn { get; set; }

    [JsonPropertyName("approvalStatus")]
    public string? ApprovalStatus { get; set; }
}

/// <summary>Maps EIS <c>VoidReceiptFilterDto</c>.</summary>
public sealed class GetVoidReceiptsRequest
{
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; init; }

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; init; }

    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; } = 50;
}

/// <summary>Maps EIS <c>GetVoidReceiptResponseDtoPaginatedResponseDto</c>.</summary>
public sealed class GetVoidReceiptsResponseData
{
    [JsonPropertyName("items")]
    public IReadOnlyList<VoidReceiptDto>? Items { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>Maps EIS <c>GetVoidReceiptResponseDto</c>.</summary>
public sealed class VoidReceiptDto
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("requestReason")]
    public string? RequestReason { get; set; }

    [JsonPropertyName("issueDate")]
    public DateOnly? IssueDate { get; set; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; set; }

    [JsonPropertyName("requestedOn")]
    public DateTime? RequestedOn { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("approvedOn")]
    public DateTime? ApprovedOn { get; set; }

    [JsonPropertyName("rejectedReason")]
    public string? RejectedReason { get; set; }
}

public sealed class SalesInvoiceSnapshotDto
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("invoiceDateTime")]
    public DateTime? InvoiceDateTime { get; set; }

    [JsonPropertyName("invoiceTotal")]
    public decimal InvoiceTotal { get; set; }

    [JsonPropertyName("fiscalCode")]
    public string? FiscalCode { get; set; }
}

/// <summary>
/// Full invoice returned by <c>POST sales/last-submitted-online-transaction</c> and offline variant.
/// Prefer <see cref="SubmittedTransactionData"/> for deserialization of live EIS responses.
/// </summary>
[Obsolete("Use SubmittedTransactionData for last-submitted-* API responses.")]
public sealed class LastSubmittedInvoiceDto
{
    [JsonPropertyName("invoiceHeader")]
    public InvoiceHeaderDto? InvoiceHeader { get; set; }

    [JsonPropertyName("invoiceLineItems")]
    public IReadOnlyList<InvoiceLineItemDto>? InvoiceLineItems { get; set; }

    [JsonPropertyName("invoiceSummary")]
    public InvoiceSummaryDto? InvoiceSummary { get; set; }

    [JsonPropertyName("dateSubmitted")]
    public DateTime? DateSubmitted { get; set; }
}
