using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Sales;

public sealed record SubmitSalesTransactionRequest
{
    [JsonPropertyName("invoiceHeader")]
    public required InvoiceHeaderDto InvoiceHeader { get; init; }

    [JsonPropertyName("invoiceLineItems")]
    public required IReadOnlyList<InvoiceLineItemDto> InvoiceLineItems { get; init; }

    [JsonPropertyName("invoiceSummary")]
    public required InvoiceSummaryDto InvoiceSummary { get; init; }
}

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

    [JsonPropertyName("isReliefSupply")]
    public bool IsReliefSupply { get; init; }

    [JsonPropertyName("vat5CertificateDetails")]
    public Vat5CertificateDetailsDto? Vat5CertificateDetails { get; init; }

    [JsonPropertyName("paymentMethod")]
    public required string PaymentMethod { get; init; }
}

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

public sealed class InvoiceLineItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

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

    [JsonPropertyName("amountTendered")]
    public decimal AmountTendered { get; init; }
}

public sealed class TaxBreakDownDto
{
    [JsonPropertyName("rateId")]
    public required string RateId { get; init; }

    [JsonPropertyName("taxableAmount")]
    public decimal TaxableAmount { get; init; }

    [JsonPropertyName("taxAmount")]
    public decimal TaxAmount { get; init; }
}

public sealed class LevyBreakDownDto
{
    [JsonPropertyName("levyTypeId")]
    public required string LevyTypeId { get; init; }

    [JsonPropertyName("levyRate")]
    public decimal LevyRate { get; init; }

    [JsonPropertyName("levyAmount")]
    public decimal LevyAmount { get; init; }
}

public sealed class SubmitSalesTransactionResponseData
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("fiscalCode")]
    public string? FiscalCode { get; set; }

    [JsonPropertyName("shouldDownloadLatestConfig")]
    public bool ShouldDownloadLatestConfig { get; set; }
}

public sealed class InvoiceNumberQueryRequest
{
    [JsonPropertyName("invoiceNumber")]
    public required string InvoiceNumber { get; init; }
}

public sealed class ProcessCreditDebitNoteRequest
{
    [JsonPropertyName("originalInvoiceNumber")]
    public required string OriginalInvoiceNumber { get; init; }

    [JsonPropertyName("noteType")]
    public required string NoteType { get; init; }

    [JsonPropertyName("invoiceHeader")]
    public required InvoiceHeaderDto InvoiceHeader { get; init; }

    [JsonPropertyName("invoiceLineItems")]
    public required IReadOnlyList<InvoiceLineItemDto> InvoiceLineItems { get; init; }

    [JsonPropertyName("invoiceSummary")]
    public required InvoiceSummaryDto InvoiceSummary { get; init; }
}

public sealed class CancelReceiptRequest
{
    [JsonPropertyName("invoiceNumber")]
    public required string InvoiceNumber { get; init; }

    [JsonPropertyName("cancellationReason")]
    public required string CancellationReason { get; init; }
}

public sealed class GetVoidReceiptsRequest
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; init; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; } = 50;
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

public sealed class VoidReceiptDto
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("voidedAt")]
    public DateTime? VoidedAt { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
