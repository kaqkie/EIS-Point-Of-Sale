using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Sales;

/// <summary>
/// Nested <c>data</c> payload for last-submitted online/offline EIS responses.
/// Maps <c>dateSubmitted</c>, <c>invoiceHeader</c>, <c>invoiceLineItems</c>, and <c>invoiceSummary</c>.
/// Lenient property binding for historical sandbox records (e.g. tax rate <c>T</c>).
/// </summary>
public sealed class SubmittedTransactionData
{
    [JsonPropertyName("dateSubmitted")]
    public DateTime? DateSubmitted { get; set; }

    [JsonPropertyName("invoiceHeader")]
    public SubmittedInvoiceHeader? InvoiceHeader { get; set; }

    [JsonPropertyName("invoiceLineItems")]
    public List<SubmittedInvoiceLineItem>? InvoiceLineItems { get; set; }

    [JsonPropertyName("invoiceSummary")]
    public SubmittedInvoiceSummary? InvoiceSummary { get; set; }
}

public sealed class SubmittedInvoiceHeader
{
    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("invoiceDateTime")]
    public DateTime? InvoiceDateTime { get; set; }

    [JsonPropertyName("sellerTIN")]
    public string? SellerTin { get; set; }

    [JsonPropertyName("buyerTIN")]
    public string? BuyerTin { get; set; }

    [JsonPropertyName("buyerName")]
    public string? BuyerName { get; set; }

    [JsonPropertyName("buyerAuthorizationCode")]
    public string? BuyerAuthorizationCode { get; set; }

    [JsonPropertyName("siteId")]
    public string? SiteId { get; set; }

    [JsonPropertyName("globalConfigVersion")]
    public int GlobalConfigVersion { get; set; }

    [JsonPropertyName("taxpayerConfigVersion")]
    public int TaxpayerConfigVersion { get; set; }

    [JsonPropertyName("terminalConfigVersion")]
    public int TerminalConfigVersion { get; set; }

    [JsonPropertyName("isReliefSupply")]
    public bool IsReliefSupply { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }
}

public sealed class SubmittedInvoiceLineItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

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
    public bool IsProduct { get; set; } = true;
}

public sealed class SubmittedInvoiceSummary
{
    [JsonPropertyName("taxBreakDown")]
    public List<SubmittedTaxBreakDown>? TaxBreakDown { get; set; }

    [JsonPropertyName("levyBreakDown")]
    public List<SubmittedLevyBreakDown>? LevyBreakDown { get; set; }

    [JsonPropertyName("totalVAT")]
    public decimal TotalVat { get; set; }

    [JsonPropertyName("offlineSignature")]
    public string? OfflineSignature { get; set; }

    [JsonPropertyName("invoiceTotal")]
    public decimal InvoiceTotal { get; set; }

    [JsonPropertyName("amountTendered")]
    public decimal AmountTendered { get; set; }
}

public sealed class SubmittedTaxBreakDown
{
    [JsonPropertyName("rateId")]
    public string? RateId { get; set; }

    [JsonPropertyName("taxableAmount")]
    public decimal TaxableAmount { get; set; }

    [JsonPropertyName("taxAmount")]
    public decimal TaxAmount { get; set; }
}

public sealed class SubmittedLevyBreakDown
{
    [JsonPropertyName("levyTypeId")]
    public string? LevyTypeId { get; set; }

    [JsonPropertyName("levyRate")]
    public decimal LevyRate { get; set; }

    [JsonPropertyName("levyAmount")]
    public decimal LevyAmount { get; set; }
}
