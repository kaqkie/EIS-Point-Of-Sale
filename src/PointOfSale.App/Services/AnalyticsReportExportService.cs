using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace PointOfSale.App.Services;

public interface IAnalyticsReportExportService
{
    Task<string> ExportTaxReconciliationCsvAsync(TaxReconciliationReport report, CancellationToken cancellationToken = default);
    Task<string> ExportZReportCsvAsync(ZReportBundle report, CancellationToken cancellationToken = default);
    Task ExportTaxReconciliationPdfAsync(TaxReconciliationReport report, CancellationToken cancellationToken = default);
    Task ExportZReportPdfAsync(ZReportBundle report, CancellationToken cancellationToken = default);
}

public sealed class AnalyticsReportExportService : IAnalyticsReportExportService
{
    public async Task<string> ExportTaxReconciliationCsvAsync(
        TaxReconciliationReport report,
        CancellationToken cancellationToken = default)
    {
        var path = PromptSavePath($"TaxReconciliation_{report.Period}_{report.LocalBusinessDate:yyyyMMdd}.csv", "CSV|*.csv");
        if (path is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Period,BusinessDate,TaxRateId,Category,TaxableTotal,VatCollected,InvoiceCount");
        foreach (var bucket in report.TaxBuckets)
        {
            sb.AppendLine(
                $"{report.Period},{report.LocalBusinessDate:yyyy-MM-dd},{bucket.TaxRateId},{bucket.Category},{bucket.TaxableTotal:0.00},{bucket.VatCollected:0.00},{bucket.InvoiceCount}");
        }

        sb.AppendLine();
        sb.AppendLine($"GrossSales,,,{report.GrossSales:0.00}");
        sb.AppendLine($"OnlineGrossSales,,,{report.OnlineGrossSales:0.00}");
        sb.AppendLine($"OnlineInvoices,,,{report.OnlineInvoiceCount}");
        sb.AppendLine($"OfflineSyncedGrossSales,,,{report.OfflineSyncedGrossSales:0.00}");
        sb.AppendLine($"OfflineSyncedInvoices,,,{report.OfflineSyncedInvoiceCount}");
        sb.AppendLine($"StandardTaxable,,,{report.StandardRateTaxable:0.00}");
        sb.AppendLine($"ZeroRatedTaxable,,,{report.ZeroRatedTaxable:0.00}");
        sb.AppendLine($"ExemptTaxable,,,{report.ExemptTaxable:0.00}");
        sb.AppendLine($"ExpectedVat17_5,,,{report.ExpectedStandardVat:0.00}");
        sb.AppendLine($"ActualVat,,,{report.ActualVatCollected:0.00}");
        sb.AppendLine($"OnlineVat,,,{report.OnlineVat:0.00}");
        sb.AppendLine($"OfflineSyncedVat,,,{report.OfflineSyncedVat:0.00}");
        sb.AppendLine($"VatVariance,,,{report.VatVariance:0.00}");
        sb.AppendLine($"Balanced,,,{report.IsBalanced}");

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportZReportCsvAsync(ZReportBundle report, CancellationToken cancellationToken = default)
    {
        var path = PromptSavePath($"ZReport_Shift{report.ShiftId}_{DateTime.Now:yyyyMMddHHmm}.csv", "CSV|*.csv");
        if (path is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("ShiftId,Cashier,OpenedAtUtc,OpeningFloat,CashSales,CardSales,MobileMoney,CashIn,CashOut,CashDrop,GrossSales,TotalVat,ExpectedCash,ClosingCash,Variance");
        sb.AppendLine(
            $"{report.ShiftId},{Escape(report.CashierName)},{report.OpenedAtUtc:O},{report.OpeningFloat:0.00},{report.CashSales:0.00},{report.CardSales:0.00},{report.MobileMoneySales:0.00},{report.CashInTotal:0.00},{report.CashOutTotal:0.00},{report.CashDropTotal:0.00},{report.GrossSales:0.00},{report.TotalVat:0.00},{report.ExpectedCashInDrawer:0.00},{report.ClosingCashCounted:0.00},{report.CashVariance:0.00}");
        sb.AppendLine();
        sb.AppendLine("InvoiceNumber,PaymentMethod,Total,VAT,FiscalSignature");
        foreach (var inv in report.FiscalizedInvoices)
        {
            sb.AppendLine(
                $"{Escape(inv.InvoiceNumber)},{Escape(inv.PaymentMethod)},{inv.InvoiceTotal:0.00},{inv.TotalVat:0.00},{Escape(inv.FiscalSignature)}");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public Task ExportTaxReconciliationPdfAsync(
        TaxReconciliationReport report,
        CancellationToken cancellationToken = default)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            PagePadding = new Thickness(40)
        };
        doc.Blocks.Add(Heading("Tax Reconciliation Audit Report"));
        doc.Blocks.Add(Line($"Period: {report.Period}"));
        doc.Blocks.Add(Line($"Business date: {report.LocalBusinessDate:yyyy-MM-dd}"));
        doc.Blocks.Add(Line($"Window (UTC): {report.FromUtc:u} → {report.ToUtcExclusive:u}"));
        doc.Blocks.Add(Line($"Synced invoices: {report.SyncedInvoiceCount} (online {report.OnlineInvoiceCount}, offline-synced {report.OfflineSyncedInvoiceCount})"));
        doc.Blocks.Add(Line($"Gross sales: {report.GrossSales:N2} (online {report.OnlineGrossSales:N2} / offline-synced {report.OfflineSyncedGrossSales:N2})"));
        doc.Blocks.Add(Line($"VAT declared: {report.TotalVatDeclared:N2} (online {report.OnlineVat:N2} / offline-synced {report.OfflineSyncedVat:N2})"));
        doc.Blocks.Add(Line($"Standard taxable (17.5%): {report.StandardRateTaxable:N2}"));
        doc.Blocks.Add(Line($"Zero-rated taxable: {report.ZeroRatedTaxable:N2}"));
        doc.Blocks.Add(Line($"Exempt taxable: {report.ExemptTaxable:N2}"));
        doc.Blocks.Add(Line($"Expected VAT: {report.ExpectedStandardVat:N2}"));
        doc.Blocks.Add(Line($"Actual VAT: {report.ActualVatCollected:N2}"));
        doc.Blocks.Add(Line($"Variance: {report.VatVariance:N2}  Balanced={report.IsBalanced}"));
        doc.Blocks.Add(Spacer());
        foreach (var bucket in report.TaxBuckets)
        {
            doc.Blocks.Add(Line(
                $"{bucket.TaxRateId} ({bucket.Category}): taxable {bucket.TaxableTotal:N2}, VAT {bucket.VatCollected:N2}, invoices {bucket.InvoiceCount}"));
        }

        PrintFlowDocument(doc, "Tax Reconciliation Report");
        return Task.CompletedTask;
    }

    public Task ExportZReportPdfAsync(ZReportBundle report, CancellationToken cancellationToken = default)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            PagePadding = new Thickness(36)
        };
        doc.Blocks.Add(Heading($"Z-Report — Shift {report.ShiftId}"));
        doc.Blocks.Add(Line($"Cashier: {report.CashierName}"));
        doc.Blocks.Add(Line($"Opened: {report.OpenedAtUtc:u}"));
        doc.Blocks.Add(Line($"Closed: {report.ClosedAtUtc:u}"));
        doc.Blocks.Add(Spacer());
        doc.Blocks.Add(Line($"Opening float: {report.OpeningFloat:N2}"));
        doc.Blocks.Add(Line($"Cash sales:    {report.CashSales:N2}"));
        doc.Blocks.Add(Line($"Card sales:    {report.CardSales:N2}"));
        doc.Blocks.Add(Line($"Mobile money:  {report.MobileMoneySales:N2}"));
        doc.Blocks.Add(Line($"Gross sales:   {report.GrossSales:N2}"));
        doc.Blocks.Add(Line($"Total VAT:     {report.TotalVat:N2}"));
        doc.Blocks.Add(Line($"Cash in/out:   {report.CashInTotal:N2} / {report.CashOutTotal:N2}"));
        doc.Blocks.Add(Line($"Cash drops:    {report.CashDropTotal:N2}"));
        doc.Blocks.Add(Line($"Expected cash: {report.ExpectedCashInDrawer:N2}"));
        doc.Blocks.Add(Line($"Counted cash:  {report.ClosingCashCounted:N2}"));
        doc.Blocks.Add(Line($"Variance:      {report.CashVariance:N2}"));
        doc.Blocks.Add(Spacer());
        doc.Blocks.Add(Line("Fiscalized invoices:"));
        foreach (var inv in report.FiscalizedInvoices.Take(100))
        {
            doc.Blocks.Add(Line(
                $"{inv.InvoiceNumber}  {inv.PaymentMethod,-12} {inv.InvoiceTotal,10:N2}  {Truncate(inv.FiscalSignature, 24)}"));
        }

        PrintFlowDocument(doc, $"Z-Report Shift {report.ShiftId}");
        return Task.CompletedTask;
    }

    private static string? PromptSavePath(string fileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void PrintFlowDocument(FlowDocument document, string description)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true)
        {
            return;
        }

        document.PageHeight = printDialog.PrintableAreaHeight;
        document.PageWidth = printDialog.PrintableAreaWidth;
        printDialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, description);
    }

    private static Paragraph Heading(string text) =>
        new(new Run(text) { FontWeight = FontWeights.Bold, FontSize = 16 }) { Margin = new Thickness(0, 0, 0, 8) };

    private static Paragraph Line(string text) =>
        new(new Run(text)) { Margin = new Thickness(0, 0, 0, 2) };

    private static Paragraph Spacer() => new(new Run(" ")) { Margin = new Thickness(0, 6, 0, 6) };

    private static string Escape(string? value) =>
        value is null ? string.Empty : $"\"{value.Replace("\"", "\"\"")}\"";

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
