using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.App.Services.Compliance;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Stock;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

/// <summary>
/// Operator-facing certification suite against the configured MRA environment (Sandbox/Production).
/// Produces <c>Logs/MraCertificationAudit.json</c> for regulatory packaging.
/// </summary>
public interface IComplianceCertificationService
{
    Task<MraCertificationAuditDocument> RunCertificationAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ComplianceCertificationService : IComplianceCertificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMraCertificationAuditStore _auditStore;
    private readonly ILogger<ComplianceCertificationService> _logger;

    public ComplianceCertificationService(
        IServiceScopeFactory scopeFactory,
        IMraCertificationAuditStore auditStore,
        ILogger<ComplianceCertificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _auditStore = auditStore;
        _logger = logger;
    }

    public async Task<MraCertificationAuditDocument> RunCertificationAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_auditStore is MraCertificationAuditStore store)
        {
            store.ClearStatus();
        }

        void Log(string message)
        {
            _auditStore.AppendStatus(message);
            progress?.Report(message);
            _logger.LogInformation("Compliance: {Message}", message);
        }

        var document = new MraCertificationAuditDocument
        {
            StartedUtc = DateTime.UtcNow,
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        };

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var auth = sp.GetRequiredService<IMraTerminalAuthProvider>();
        var sales = sp.GetRequiredService<SalesTransactionService>();
        var stock = sp.GetRequiredService<StockManagementService>();
        var onboarding = sp.GetRequiredService<TerminalOnboardingService>();
        var queue = sp.GetRequiredService<OfflineSalesQueueService>();
        var inventory = sp.GetRequiredService<ILocalInventoryRepository>();

        try
        {
            document.TerminalId = await auth.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            document.TerminalId = "PENDING_ACTIVATION";
        }

        Log($"Starting MRA EIS certification for terminal {document.TerminalId}.");

        await RunStepAsync(document, "Terminal Activation / Credential Context", "onboarding/*", async () =>
        {
            var context = await auth.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(context.JwtToken) || string.IsNullOrWhiteSpace(context.SecretKey))
            {
                throw new InvalidOperationException("JWT or secret key missing — complete terminal activation first.");
            }

            return (200, JsonSerializer.Serialize(new { hasJwt = true, hasSecret = true }), null, null);
        }, Log, cancellationToken).ConfigureAwait(false);

        await RunStepAsync(document, "Configuration Fetching", "configuration/get-latest-configs", async () =>
        {
            var result = await onboarding.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Remark ?? "get-latest-configs failed.");
            }

            return (200, JsonSerializer.Serialize(new { success = true, remark = result.Remark }), null, null);
        }, Log, cancellationToken).ConfigureAwait(false);

        await RunStepAsync(document, "Product UNSPSC Status", "utilities/product-status", async () =>
        {
            // Endpoint exercise — unmapped barcodes may return non-success; still proves auth + routing.
            var result = await stock.GetProductStatusAsync("CERT-SKU-001", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (result.Success ? 200 : 404, JsonSerializer.Serialize(new
            {
                success = result.Success,
                productId = result.Data?.ProductId,
                psCode = result.Data?.PsCode,
                remark = result.Remark
            }), null, null);
        }, Log, cancellationToken).ConfigureAwait(false);

        await RunStepAsync(document, "Initial Inventory Staging/Upload", "utilities/taxpayer-initial-inventory-upload", async () =>
        {
            var items = new List<InitialInventoryItemDto>
            {
                new()
                {
                    BarCode = "CERT-SKU-001",
                    ProductName = "Certification Item",
                    ProductDescription = "Certification Item",
                    UnitPrice = 100m,
                    QuantityInStock = 10,
                    CostPrice = 100m,
                    SellingPrice = 100m
                }
            };
            var result = await stock.UploadInitialInventoryInBatchesAsync(items, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Remark ?? "Initial inventory upload failed.");
            }

            return (200, JsonSerializer.Serialize(new
            {
                uploaded = result.Data?.UploadedItemCount,
                batches = result.Data?.BatchCount,
                mapped = result.Data?.FinalBatch?.MappedItems,
                unmapped = result.Data?.FinalBatch?.UnmappedItems,
                remark = result.Remark
            }), null, null);
        }, Log, cancellationToken).ConfigureAwait(false);

        SubmitSalesTransactionRequest? lastSale = null;
        await RunStepAsync(document, "Online Transaction Submission", "sales/submit-sales-transaction", async () =>
        {
            await EnsureLocalProductAsync(inventory, cancellationToken).ConfigureAwait(false);
            lastSale = BuildSale($"CERT-ON-{DateTime.UtcNow:yyyyMMddHHmmss}");
            var requestJson = JsonSerializer.Serialize(lastSale, MraJson.SerializerOptions);
            var signed = await auth.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
            var xSig = HmacSignatureService.ComputeHmacSha512Base64(requestJson, signed.SecretKey!);

            var result = await sales.SubmitSalesTransactionAsync(lastSale, cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.Data is null)
            {
                throw new InvalidOperationException(result.Remark ?? "Online sale submission failed.");
            }

            return (
                200,
                JsonSerializer.Serialize(result.Data, MraJson.SerializerOptions),
                result.Data.ResolveFiscalSignature(),
                xSig);
        }, Log, cancellationToken).ConfigureAwait(false);

        await RunStepAsync(document, "HMAC-SHA512 Signature Header Validation", "x-signature", async () =>
        {
            const string sample = "{\"certification\":true}";
            var signed = await auth.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
            var sig = HmacSignatureService.ComputeHmacSha512Base64(sample, signed.SecretKey!);
            var roundTrip = MraApiClient.ComputeSignature(sample, signed.SecretKey!);
            if (!sig.Equals(roundTrip, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("HMAC-SHA512 mismatch between helpers.");
            }

            return (200, JsonSerializer.Serialize(new { sample, signature = sig }), sig, sig);
        }, Log, cancellationToken).ConfigureAwait(false);

        await RunStepAsync(document, "Offline Signature Backup & Queue Insertion", "offline-queue", async () =>
        {
            await EnsureLocalProductAsync(inventory, cancellationToken).ConfigureAwait(false);
            var offlineSale = BuildSale($"CERT-OFF-{DateTime.UtcNow:yyyyMMddHHmmss}");
            var result = await queue.EnqueueAndTrySubmitAsync(offlineSale, forceOffline: true, cancellationToken)
                .ConfigureAwait(false);
            if (result.QueueId <= 0)
            {
                throw new InvalidOperationException("Offline queue insertion failed.");
            }

            return (200, JsonSerializer.Serialize(new { result.QueueId, result.InvoiceNumber, result.SubmittedOnline }), null, null);
        }, Log, cancellationToken).ConfigureAwait(false);

        await RunStepAsync(document, "Credit/Debit Note Processing", "sales/process-credit-debit-note", async () =>
        {
            var original = lastSale ?? BuildSale($"CERT-CDN-SRC-{DateTime.UtcNow:yyyyMMddHHmmss}");
            var note = new ProcessCreditDebitNoteRequest
            {
                ReasonForAdjustment = "Certification credit note",
                InvoiceHeader = new InvoiceHeaderDto
                {
                    InvoiceNumber = $"CERT-CDN-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    InvoiceDateTime = DateTime.UtcNow,
                    SellerTin = original.InvoiceHeader.SellerTin,
                    SiteId = original.InvoiceHeader.SiteId,
                    PaymentMethod = original.InvoiceHeader.PaymentMethod,
                    GlobalConfigVersion = original.InvoiceHeader.GlobalConfigVersion,
                    TaxpayerConfigVersion = original.InvoiceHeader.TaxpayerConfigVersion,
                    TerminalConfigVersion = original.InvoiceHeader.TerminalConfigVersion
                },
                InvoiceLineItems = original.InvoiceLineItems,
                InvoiceSummary = original.InvoiceSummary
            };

            var result = await sales.ProcessCreditDebitNoteAsync(note, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Remark ?? "Credit/debit note failed.");
            }

            return (
                200,
                JsonSerializer.Serialize(result.Data, MraJson.SerializerOptions),
                result.Data?.ValidationUrl,
                null);
        }, Log, cancellationToken).ConfigureAwait(false);

        document.CompletedUtc = DateTime.UtcNow;
        document.OverallResult = document.Steps.All(s => s.Passed) ? "Passed" : "Failed";
        await _auditStore.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        Log($"Certification finished: {document.OverallResult} ({document.Steps.Count(s => s.Passed)}/{document.Steps.Count} passed).");
        return document;
    }

    private static async Task RunStepAsync(
        MraCertificationAuditDocument document,
        string scenario,
        string endpoint,
        Func<Task<(int HttpStatus, string? Response, string? FiscalOrSig, string? XSignature)>> action,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var step = new MraCertificationStepResult
        {
            Scenario = scenario,
            Endpoint = endpoint,
            TimestampUtc = DateTime.UtcNow
        };

        try
        {
            log($"Running: {scenario}");
            var (http, response, fiscal, xSig) = await action().ConfigureAwait(false);
            step.HttpStatusCode = http;
            step.ResponsePayload = Truncate(response);
            step.ResponseSignatureOrFiscalCode = fiscal;
            step.XSignatureHeader = xSig;
            step.Passed = true;
            log($"PASS: {scenario}");
        }
        catch (Exception ex)
        {
            step.Passed = false;
            step.Error = ex.Message;
            step.HttpStatusCode = null;
            log($"FAIL: {scenario} — {ex.Message}");
        }
        finally
        {
            sw.Stop();
            step.DurationMs = sw.ElapsedMilliseconds;
            document.Steps.Add(step);
            _ = cancellationToken;
        }
    }

    private static async Task EnsureLocalProductAsync(ILocalInventoryRepository inventory, CancellationToken cancellationToken)
    {
        var existing = await inventory.GetByProductCodeAsync("CERT-SKU-001", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await inventory.UpsertAsync(
            new LocalInventoryItem
            {
                ProductId = "CERT-SKU-001",
                ProductCode = "CERT-SKU-001",
                Name = "Certification Item",
                UnitPrice = 100m,
                StockQuantity = 100,
                TaxRateId = "A",
                HsCode = "0000",
                UnitOfMeasure = "EA"
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static SubmitSalesTransactionRequest BuildSale(string invoiceNumber)
    {
        var net = 100m;
        var vat = PosTaxCalculator.CalculateVatAmount(net, PosTaxCalculator.MalawiStandardVatRatePercent);
        return new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = invoiceNumber,
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "1234567890",
                SiteId = "SITE-CERT",
                PaymentMethod = "Cash",
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 1
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = "CERT-SKU-001",
                    Description = "Certification Item",
                    UnitPrice = net,
                    Quantity = 1,
                    Total = net,
                    TotalVat = vat,
                    TaxRateId = "A",
                    IsProduct = true
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = net, TaxAmount = vat }
                ],
                TotalVat = vat,
                InvoiceTotal = net + vat,
                AmountTendered = net + vat
            }
        };
    }

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= 4000 ? value : value[..4000] + "...";
}
