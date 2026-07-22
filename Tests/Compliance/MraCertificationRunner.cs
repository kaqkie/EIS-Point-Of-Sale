using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using PointOfSale.App.Services.Compliance;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Stock;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests.Compliance;

/// <summary>
/// MRA EIS compliance certification harness — executes mandatory sandbox scenarios and writes
/// <c>Logs/MraCertificationAudit.json</c> for regulatory packaging.
/// </summary>
public sealed class MraCertificationRunner : IDisposable
{
    private readonly MockMraServer _mock;
    private readonly MraIntegrationHarness _harness;
    private readonly MraCertificationAuditStore _auditStore;
    private readonly string _auditDirectory;

    public MraCertificationRunner()
    {
        _mock = new MockMraServer();
        _mock.ConfigureCertificationEndpoints();
        _harness = new MraIntegrationHarness(_mock);
        _auditDirectory = Path.Combine(Path.GetTempPath(), "ART_Cert_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_auditDirectory);
        // Point audit file under temp via symlink-style redirect: write relative to AppContext after redirecting BaseDirectory is hard;
        // we save explicitly via store path override by writing after RunAllAsync using store API under AppContext.
        _auditStore = new MraCertificationAuditStore();
    }

    public MraCertificationAuditDocument? LastDocument { get; private set; }

    public async Task<MraCertificationAuditDocument> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var document = new MraCertificationAuditDocument
        {
            StartedUtc = DateTime.UtcNow,
            TerminalId = _harness.AuthProvider.TerminalId,
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        };

        _harness.InventoryRepository.Seed(new LocalInventoryItem
        {
            ProductId = "CERT-SKU-001",
            ProductCode = "CERT-SKU-001",
            Name = "Certification Item",
            UnitPrice = 100m,
            StockQuantity = 100,
            TaxRateId = "A",
            HsCode = "0000",
            UnitOfMeasure = "EA"
        });

        await StepAsync(document, "Terminal Activation", "onboarding/activate-terminal", async () =>
        {
            var response = await _harness.ApiClient.PostAsync<object, object>(
                "onboarding/activate-terminal",
                new { terminalActivationCode = "TAC-CERT-001" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Assert.True(response.IsSuccess);
            return (200, JsonSerializer.Serialize(response), null, null, JsonSerializer.Serialize(new { tac = "TAC-CERT-001" }));
        }, cancellationToken).ConfigureAwait(false);

        await StepAsync(document, "Configuration Fetching", "configuration/get-latest-configs", async () =>
        {
            var response = await _harness.ApiClient.GetLatestConfigsAsync<object>(
                _harness.AuthProvider.JwtToken,
                cancellationToken).ConfigureAwait(false);
            Assert.True(response.IsSuccess);
            return (200, JsonSerializer.Serialize(response), null, null, null);
        }, cancellationToken).ConfigureAwait(false);

        await StepAsync(document, "Initial Inventory Staging/Upload", "stock/upload-initial-inventory", async () =>
        {
            var items = Enumerable.Range(1, 55)
                .Select(i => new InitialInventoryItemDto
                {
                    ProductCode = $"CERT-{i:D3}",
                    ProductName = $"Item {i}",
                    UnitPrice = 10m,
                    OpeningStockQuantity = 5,
                    TaxRateId = "A"
                })
                .ToList();

            var result = await _harness.StockService.UploadInitialInventoryInBatchesAsync(items, cancellationToken)
                .ConfigureAwait(false);
            Assert.True(result.Success);
            Assert.Equal(55, result.Data);
            Assert.True(_mock.InitialInventoryRequests.Count >= 2);
            return (200, JsonSerializer.Serialize(new { uploaded = result.Data, batches = _mock.InitialInventoryRequests.Count }), null, null,
                JsonSerializer.Serialize(new { itemCount = 55 }));
        }, cancellationToken).ConfigureAwait(false);

        string? onlineXSignature = null;
        await StepAsync(document, "Online Transaction Submission", "sales/submit-sales-transaction", async () =>
        {
            var sale = BuildSale("CERT-ONLINE-001");
            var body = JsonSerializer.Serialize(sale, MraJson.SerializerOptions);
            onlineXSignature = HmacSignatureService.ComputeHmacSha512Base64(body, _harness.AuthProvider.SecretKey);
            var result = await _harness.SalesService.SubmitSalesTransactionAsync(sale, cancellationToken)
                .ConfigureAwait(false);
            Assert.True(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Data?.ResolveFiscalSignature()));

            var logged = _mock.SalesRequests.Last();
            var sent = logged.Headers
                .First(h => h.Key.Equals(HmacSignatureService.SignatureHeaderName, StringComparison.OrdinalIgnoreCase))
                .Value.First();
            Assert.Equal(onlineXSignature, sent);

            return (200, JsonSerializer.Serialize(result.Data, MraJson.SerializerOptions),
                result.Data!.ResolveFiscalSignature(), sent, body);
        }, cancellationToken).ConfigureAwait(false);

        await StepAsync(document, "HMAC-SHA512 Signature Header Validation", "x-signature", async () =>
        {
            const string plain = "{\"certificationProbe\":true}";
            var expected = HmacSignatureService.ComputeHmacSha512Base64(plain, _harness.AuthProvider.SecretKey);
            var actual = PointOfSale.Infrastructure.Services.MraApiClient.ComputeSignature(plain, _harness.AuthProvider.SecretKey);
            Assert.Equal(expected, actual);
            Assert.False(string.IsNullOrWhiteSpace(onlineXSignature));
            return (200, JsonSerializer.Serialize(new { plain, expected }), expected, expected, plain);
        }, cancellationToken).ConfigureAwait(false);

        await StepAsync(document, "Offline Signature Backup & Queue Insertion", "offline-queue", async () =>
        {
            var sale = BuildSale("CERT-OFFLINE-001");
            var unsigned = sale with { InvoiceSummary = sale.InvoiceSummary with { OfflineSignature = null } };
            var payloadJson = JsonSerializer.Serialize(unsigned, MraJson.SerializerOptions);
            var offlineSig = await _harness.SalesService.ComputeOfflineSignatureAsync(payloadJson, cancellationToken)
                .ConfigureAwait(false);
            Assert.Equal(
                HmacSignatureService.ComputeHmacSha512Base64(payloadJson, _harness.AuthProvider.SecretKey),
                offlineSig);

            var queueResult = await _harness.OfflineQueueService
                .EnqueueAndTrySubmitAsync(sale, forceOffline: true, cancellationToken)
                .ConfigureAwait(false);
            Assert.True(queueResult.QueueId > 0);
            Assert.False(queueResult.SubmittedOnline);

            var item = await _harness.QueueRepository.GetByIdAsync(queueResult.QueueId, cancellationToken)
                .ConfigureAwait(false);
            Assert.NotNull(item);
            Assert.Equal("PENDING", item!.Status);

            return (200,
                JsonSerializer.Serialize(new { queueResult.QueueId, offlineSig }),
                offlineSig,
                offlineSig,
                payloadJson);
        }, cancellationToken).ConfigureAwait(false);

        await StepAsync(document, "Credit/Debit Note Processing", "sales/process-credit-debit-note", async () =>
        {
            var note = new ProcessCreditDebitNoteRequest
            {
                OriginalInvoiceNumber = "CERT-ONLINE-001",
                NoteType = "Credit",
                InvoiceHeader = new InvoiceHeaderDto
                {
                    InvoiceNumber = "CERT-CDN-001",
                    InvoiceDateTime = DateTime.UtcNow,
                    SellerTin = "1234567890",
                    SiteId = "SITE-CERT",
                    PaymentMethod = "Cash",
                    GlobalConfigVersion = 1,
                    TaxpayerConfigVersion = 1,
                    TerminalConfigVersion = 1
                },
                InvoiceLineItems = BuildSale("CERT-CDN-001").InvoiceLineItems,
                InvoiceSummary = BuildSale("CERT-CDN-001").InvoiceSummary
            };

            var result = await _harness.SalesService.ProcessCreditDebitNoteAsync(note, cancellationToken)
                .ConfigureAwait(false);
            Assert.True(result.Success);
            return (200, JsonSerializer.Serialize(result.Data, MraJson.SerializerOptions),
                result.Data?.ResolveFiscalSignature(), null, JsonSerializer.Serialize(note, MraJson.SerializerOptions));
        }, cancellationToken).ConfigureAwait(false);

        document.CompletedUtc = DateTime.UtcNow;
        document.OverallResult = document.Steps.All(s => s.Passed) ? "Passed" : "Failed";
        await PersistAuditAsync(document, cancellationToken).ConfigureAwait(false);
        LastDocument = document;
        return document;
    }

    private async Task PersistAuditAsync(MraCertificationAuditDocument document, CancellationToken cancellationToken)
    {
        // Write to app-relative Logs path (test output directory) and a stable temp copy for packaging demos.
        await _auditStore.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        var copyPath = Path.Combine(_auditDirectory, "MraCertificationAudit.json");
        File.Copy(_auditStore.AuditFilePath, copyPath, overwrite: true);
    }

    private static async Task StepAsync(
        MraCertificationAuditDocument document,
        string scenario,
        string endpoint,
        Func<Task<(int Http, string? Response, string? Fiscal, string? XSig, string? Request)>> action,
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
            var (http, response, fiscal, xSig, request) = await action().ConfigureAwait(false);
            step.HttpStatusCode = http;
            step.ResponsePayload = Truncate(response);
            step.ResponseSignatureOrFiscalCode = fiscal;
            step.XSignatureHeader = xSig;
            step.RequestPayload = Truncate(request);
            step.Passed = true;
        }
        catch (Exception ex)
        {
            step.Passed = false;
            step.Error = ex.ToString();
        }
        finally
        {
            sw.Stop();
            step.DurationMs = sw.ElapsedMilliseconds;
            document.Steps.Add(step);
            _ = cancellationToken;
        }
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

    public void Dispose()
    {
        _harness.Dispose();
        _mock.Dispose();
        try
        {
            if (Directory.Exists(_auditDirectory))
            {
                Directory.Delete(_auditDirectory, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}

public sealed class MraCertificationRunnerTests
{
    [Fact]
    public async Task RunAllAsync_AllMandatoryScenarios_Pass_AndWritesAuditJson()
    {
        using var runner = new MraCertificationRunner();
        var document = await runner.RunAllAsync();

        Assert.Equal("Passed", document.OverallResult);
        var failedSteps = document.Steps.Where(s => !s.Passed).ToList();
        Assert.True(failedSteps.Count == 0, string.Join(Environment.NewLine, failedSteps.Select(s => $"{s.Scenario}: {s.Error}")));
        Assert.Equal(7, document.Steps.Count);
        Assert.All(document.Steps, s => Assert.True(s.Passed, s.Error));

        var auditPath = Path.Combine(AppContext.BaseDirectory, "Logs", "MraCertificationAudit.json");
        Assert.True(File.Exists(auditPath));
        var json = await File.ReadAllTextAsync(auditPath);
        Assert.Contains("Online Transaction Submission", json, StringComparison.Ordinal);
        Assert.Contains("HMAC-SHA512", json, StringComparison.Ordinal);
    }
}
