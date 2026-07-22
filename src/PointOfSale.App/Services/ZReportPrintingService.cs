using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Pricing;

namespace PointOfSale.App.Services;

public interface IZReportPrintingService
{
    byte[] BuildEscPosPayload(ZReportBundle report, ZReportPrintContext? context = null);

    Task<ZReportPrintResult> PrintAsync(
        ZReportBundle report,
        ZReportPrintContext? context = null,
        CancellationToken cancellationToken = default);

    string FormatPlainText(ZReportBundle report, ZReportPrintContext? context = null);
}

public sealed class ZReportPrintContext
{
    public string TradingName { get; init; } = "Albert Retail Terminal";
    public string BranchId { get; init; } = string.Empty;
    public string SiteId { get; init; } = string.Empty;
    public string TerminalId { get; init; } = string.Empty;
    public string ManagerSignOff { get; init; } = string.Empty;
    public DateTime? BusinessDate { get; init; }
    public decimal CumulativeGrossSalesMwk { get; init; }
    public decimal CumulativeVatMwk { get; init; }
    public decimal TotalVoidsMwk { get; init; }
    public int VoidCount { get; init; }
    public bool AuditPassed { get; init; } = true;
    public string AuditMessage { get; init; } = string.Empty;
}

public sealed class ZReportPrintResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool UsedEscPos { get; init; }
}

/// <summary>
/// Thermal ESC/POS formatter for MRA-oriented Z-Report / EOD fiscal closure sheets.
/// </summary>
public sealed class ZReportPrintingService : IZReportPrintingService
{
    private readonly IThermalPrinterHardwareService _printer;
    private readonly ThermalPrinterOptions _thermalOptions;
    private readonly TerminalDeploymentOptions _terminalOptions;
    private readonly ILogger<ZReportPrintingService> _logger;

    public ZReportPrintingService(
        IThermalPrinterHardwareService printer,
        IOptions<ThermalPrinterOptions> thermalOptions,
        IOptions<TerminalDeploymentOptions> terminalOptions,
        ILogger<ZReportPrintingService> logger)
    {
        _printer = printer;
        _thermalOptions = thermalOptions.Value;
        _terminalOptions = terminalOptions.Value;
        _logger = logger;
    }

    public byte[] BuildEscPosPayload(ZReportBundle report, ZReportPrintContext? context = null)
    {
        var ctx = EnrichContext(context);
        return EscPosZReportEncoder.Encode(report, ctx, _thermalOptions.CharactersPerLineResolved);
    }

    public string FormatPlainText(ZReportBundle report, ZReportPrintContext? context = null)
    {
        var ctx = EnrichContext(context);
        return EscPosZReportEncoder.FormatPlainText(report, ctx, _thermalOptions.CharactersPerLineResolved);
    }

    public async Task<ZReportPrintResult> PrintAsync(
        ZReportBundle report,
        ZReportPrintContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var ctx = EnrichContext(context);

        try
        {
            if (_thermalOptions.Enabled && _thermalOptions.PreferEscPos)
            {
                var payload = BuildEscPosPayload(report, ctx);
                await _printer.PrintRawAsync(payload, $"ZReport-{report.ShiftId}-{DateTime.Now:yyyyMMddHHmm}", cancellationToken)
                    .ConfigureAwait(false);
                return new ZReportPrintResult
                {
                    Success = true,
                    UsedEscPos = true,
                    Message = "Z-Report sent to thermal printer (ESC/POS)."
                };
            }

            // Fallback: print as a simple receipt-shaped document via hardware service when ESC/POS disabled.
            var text = FormatPlainText(report, ctx);
            var bytes = Encoding.UTF8.GetBytes(text);
            await _printer.PrintRawAsync(bytes, $"ZReport-Text-{DateTime.Now:yyyyMMddHHmm}", cancellationToken)
                .ConfigureAwait(false);
            return new ZReportPrintResult
            {
                Success = true,
                UsedEscPos = false,
                Message = "Z-Report printed as plain text document."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to print Z-Report for shift {ShiftId}.", report.ShiftId);
            return new ZReportPrintResult
            {
                Success = false,
                Message = $"Z-Report print failed: {ex.Message}"
            };
        }
    }

    private ZReportPrintContext EnrichContext(ZReportPrintContext? context) =>
        new()
        {
            TradingName = string.IsNullOrWhiteSpace(context?.TradingName)
                ? _terminalOptions.FallbackTradingName
                : context!.TradingName,
            BranchId = string.IsNullOrWhiteSpace(context?.BranchId) ? _terminalOptions.BranchId : context!.BranchId,
            SiteId = string.IsNullOrWhiteSpace(context?.SiteId) ? _terminalOptions.SiteId : context!.SiteId,
            TerminalId = context?.TerminalId ?? string.Empty,
            ManagerSignOff = context?.ManagerSignOff ?? string.Empty,
            BusinessDate = context?.BusinessDate,
            CumulativeGrossSalesMwk = context?.CumulativeGrossSalesMwk ?? 0m,
            CumulativeVatMwk = context?.CumulativeVatMwk ?? 0m,
            TotalVoidsMwk = context?.TotalVoidsMwk ?? 0m,
            VoidCount = context?.VoidCount ?? 0,
            AuditPassed = context?.AuditPassed ?? true,
            AuditMessage = context?.AuditMessage ?? string.Empty
        };
}

public static class EscPosZReportEncoder
{
    private static Encoding? _printerEncoding;

    private static Encoding PrinterEncoding
    {
        get
        {
            if (_printerEncoding is not null)
            {
                return _printerEncoding;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _printerEncoding = Encoding.GetEncoding(437);
            return _printerEncoding;
        }
    }

    public static byte[] Encode(ZReportBundle report, ZReportPrintContext context, int charactersPerLine)
    {
        if (charactersPerLine < 24)
        {
            charactersPerLine = 24;
        }

        using var buffer = new MemoryStream(2048);
        Write(buffer, Init());
        Write(buffer, AlignCenter());
        Write(buffer, Bold(true));
        WriteLine(buffer, Truncate(context.TradingName, charactersPerLine));
        WriteLine(buffer, "Z-REPORT / EOD CLOSURE");
        Write(buffer, Bold(false));
        Write(buffer, AlignLeft());
        WriteLine(buffer, Separator(charactersPerLine));

        var businessDate = context.BusinessDate?.ToString("yyyy-MM-dd")
            ?? report.OpenedAtUtc.ToLocalTime().ToString("yyyy-MM-dd");
        WriteLine(buffer, Truncate($"Business date: {businessDate}", charactersPerLine));
        if (!string.IsNullOrWhiteSpace(context.BranchId))
        {
            WriteLine(buffer, Truncate($"Branch: {context.BranchId}", charactersPerLine));
        }

        if (!string.IsNullOrWhiteSpace(context.SiteId))
        {
            WriteLine(buffer, Truncate($"Site: {context.SiteId}", charactersPerLine));
        }

        if (!string.IsNullOrWhiteSpace(context.TerminalId))
        {
            WriteLine(buffer, Truncate($"Terminal: {context.TerminalId}", charactersPerLine));
        }

        WriteLine(buffer, Truncate($"Cashier: {report.CashierName}", charactersPerLine));
        if (report.ShiftId > 0)
        {
            WriteLine(buffer, Truncate($"Shift #: {report.ShiftId}", charactersPerLine));
        }

        WriteLine(buffer, Truncate($"Opened: {report.OpenedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", charactersPerLine));
        if (report.ClosedAtUtc is not null)
        {
            WriteLine(buffer, Truncate($"Closed: {report.ClosedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}", charactersPerLine));
        }

        WriteLine(buffer, Separator(charactersPerLine));
        Write(buffer, Bold(true));
        WriteLine(buffer, "SALES TOTALS (MWK)");
        Write(buffer, Bold(false));
        WriteLine(buffer, Columns("Cash", Money(report.CashSales), charactersPerLine));
        WriteLine(buffer, Columns("Card", Money(report.CardSales), charactersPerLine));
        WriteLine(buffer, Columns("Mobile money", Money(report.MobileMoneySales), charactersPerLine));
        WriteLine(buffer, Columns("Other", Money(report.OtherSales), charactersPerLine));
        WriteLine(buffer, Columns("Gross sales", Money(report.GrossSales), charactersPerLine));
        WriteLine(buffer, Columns("Total VAT 17.5%", Money(report.TotalVat), charactersPerLine));
        WriteLine(buffer, Columns("Voids", $"{context.VoidCount} / {Money(context.TotalVoidsMwk)}", charactersPerLine));
        WriteLine(buffer, Columns("Invoices", report.InvoiceCount.ToString(), charactersPerLine));

        WriteLine(buffer, Separator(charactersPerLine));
        Write(buffer, Bold(true));
        WriteLine(buffer, "CASH DRAWER");
        Write(buffer, Bold(false));
        WriteLine(buffer, Columns("Opening float", Money(report.OpeningFloat), charactersPerLine));
        WriteLine(buffer, Columns("Expected cash", Money(report.ExpectedCashInDrawer), charactersPerLine));
        WriteLine(buffer, Columns("Counted cash", Money(report.ClosingCashCounted ?? 0m), charactersPerLine));
        WriteLine(buffer, Columns("Variance", Money(report.CashVariance ?? 0m), charactersPerLine));

        WriteLine(buffer, Separator(charactersPerLine));
        Write(buffer, Bold(true));
        WriteLine(buffer, "CUMULATIVE FISCAL");
        Write(buffer, Bold(false));
        WriteLine(buffer, Columns("Cum. gross", Money(context.CumulativeGrossSalesMwk), charactersPerLine));
        WriteLine(buffer, Columns("Cum. VAT", Money(context.CumulativeVatMwk), charactersPerLine));
        WriteLine(buffer, Columns("Audit", context.AuditPassed ? "PASS" : "FAIL", charactersPerLine));
        if (!string.IsNullOrWhiteSpace(context.AuditMessage))
        {
            foreach (var chunk in Chunk(context.AuditMessage, charactersPerLine))
            {
                WriteLine(buffer, chunk);
            }
        }

        WriteLine(buffer, Separator(charactersPerLine));
        WriteLine(buffer, Truncate($"Manager: {context.ManagerSignOff}", charactersPerLine));
        WriteLine(buffer, Truncate($"Printed: {DateTime.Now:yyyy-MM-dd HH:mm}", charactersPerLine));
        Write(buffer, AlignCenter());
        WriteLine(buffer, "MRA EIS Z-Report");
        WriteLine(buffer, "Albert Retail Terminal");
        Write(buffer, FeedAndCut());
        return buffer.ToArray();
    }

    public static string FormatPlainText(ZReportBundle report, ZReportPrintContext context, int charactersPerLine)
    {
        var payload = Encode(report, context, charactersPerLine);
        // Strip ESC/POS control bytes for on-screen preview / fallback text.
        var sb = new StringBuilder();
        foreach (var b in payload)
        {
            if (b is >= 32 and <= 126 or 10 or 13)
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString();
    }

    private static string Money(decimal value) => PosTaxCalculator.RoundMoney(value).ToString("N2");

    private static byte[] Init() => [0x1B, 0x40];
    private static byte[] AlignLeft() => [0x1B, 0x61, 0x00];
    private static byte[] AlignCenter() => [0x1B, 0x61, 0x01];
    private static byte[] Bold(bool on) => [0x1B, 0x45, (byte)(on ? 1 : 0)];
    private static byte[] FeedAndCut() => [0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x00];

    private static void Write(Stream stream, byte[] data) => stream.Write(data, 0, data.Length);

    private static void WriteLine(Stream stream, string text)
    {
        var bytes = PrinterEncoding.GetBytes(text + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Separator(int width) => new('-', Math.Min(width, 64));

    private static string Columns(string left, string right, int width)
    {
        left = Truncate(left, Math.Max(1, width - right.Length - 1));
        var spaces = Math.Max(1, width - left.Length - right.Length);
        return left + new string(' ', spaces) + right;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static IEnumerable<string> Chunk(string value, int size)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        for (var i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }
}
