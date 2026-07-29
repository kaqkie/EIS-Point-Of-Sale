using System.Net;
using System.Text;
using System.Text.Json;
using PointOfSale.Mra.Security;

namespace PointOfSale.Infrastructure.Testing;

/// <summary>
/// In-process HTTP mock for MRA EIS sandbox — signatures, VAT validation, timeouts, and certification routes.
/// </summary>
public class MockMraEisServer : IDisposable
{
    private readonly MockMraHttpHandler _handler = new();
    private readonly object _salesGate = new();
    private Func<string, HttpRequestMessage?, Task<HttpResponseMessage>> _salesResponder = (body, _) =>
        Task.FromResult(CreateSuccessSalesResponse(ParseInvoiceNumber(body)));

    private Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> _inventoryResponder = (_, _) =>
        Task.FromResult(CreateJsonResponse(HttpStatusCode.NotFound, new { statusCode = 0, remark = "Inventory route not configured." }));

    private string? _hmacSecretForVerification;
    private bool _rejectAllInvalidHmac;

    public MockMraEisServer()
    {
        _handler.SetResponder(ResolveResponseAsync);
        ConfigureSalesSuccessForAll();
    }

    public string BaseUrl => "https://mock-mra-eis.local/api/v1/";

    public HttpMessageHandler HttpHandler => _handler;

    public IReadOnlyList<RecordedMraEisRequest> AllRequests => _handler.Requests;

    public IReadOnlyList<RecordedMraEisRequest> SalesRequests =>
        _handler.Requests
            .Where(x => x.Path.Contains("submit-sales-transaction", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<RecordedMraEisRequest> InitialInventoryRequests =>
        _handler.Requests
            .Where(x => x.Path.Contains("upload-initial-inventory", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public void EnableHmacVerification(string terminalSecretKey, bool rejectInvalidSignatures = true)
    {
        _hmacSecretForVerification = terminalSecretKey;
        _rejectAllInvalidHmac = rejectInvalidSignatures;
    }

    public void SetSalesResponder(Func<string, HttpRequestMessage?, Task<HttpResponseMessage>> responder)
    {
        lock (_salesGate)
        {
            _salesResponder = responder;
        }
    }

    public void ConfigureSalesSuccessForAll(
        string fiscalSignature = "FSIG-SANDBOX-001",
        string verificationUrl = "https://dev-eis-portal.mra.mw/verify/test")
    {
        SetSalesResponder((body, _) =>
            Task.FromResult(CreateSuccessSalesResponse(ParseInvoiceNumber(body), fiscalSignature, verificationUrl)));
    }

    public void ConfigureSalesHttp400ForInvoice(string invoiceNumber, string remark = "MRA validation failure (simulated)")
    {
        SetSalesResponder((body, _) =>
        {
            var invoice = ParseInvoiceNumber(body);
            if (invoice.Equals(invoiceNumber, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(CreateJsonResponse(HttpStatusCode.BadRequest, new
                {
                    statusCode = 0,
                    remark,
                    errors = new[]
                    {
                        new { errorCode = 40001, fieldName = "invoiceHeader.invoiceNumber", errorMessage = remark }
                    }
                }));
            }

            return Task.FromResult(CreateSuccessSalesResponse(invoice));
        });
    }

    public void ConfigureSalesInvalidVatFormatting(string invoiceNumber)
    {
        SetSalesResponder((body, _) =>
        {
            var invoice = ParseInvoiceNumber(body);
            if (!invoice.Equals(invoiceNumber, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(CreateSuccessSalesResponse(invoice));
            }

            return Task.FromResult(CreateJsonResponse(HttpStatusCode.BadRequest, new
            {
                statusCode = 0,
                remark = "Invalid VAT formatting — tax breakdown does not match line totals.",
                errors = new[]
                {
                    new
                    {
                        errorCode = 42210,
                        fieldName = "invoiceSummary.totalVat",
                        errorMessage = "VAT amount must equal 17.5% of taxable base (Malawi standard rate)."
                    }
                }
            }));
        });
    }

    public void ConfigureSalesMismatchedHmacResponse()
    {
        SetSalesResponder((body, request) =>
        {
            if (request is not null && !ValidateHmacIfEnabled(request, body))
            {
                return Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new
                {
                    statusCode = 0,
                    remark = "x-signature HMAC does not match request body.",
                    errors = new[]
                    {
                        new { errorCode = 40101, fieldName = "x-signature", errorMessage = "Invalid HMAC-SHA512 token." }
                    }
                }));
            }

            return Task.FromResult(CreateSuccessSalesResponse(ParseInvoiceNumber(body)));
        });
    }

    public void ConfigureSalesTimeout(TimeSpan delay)
    {
        SetSalesResponder(async (_, _) =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            return CreateJsonResponse(HttpStatusCode.OK, new { statusCode = 1, remark = "late", data = new { } });
        });
    }

    public void ConfigureInitialInventorySuccess()
    {
        _inventoryResponder = (_, _) => Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, new
        {
            statusCode = 1,
            remark = "Batch accepted",
            data = new { acceptedCount = 50 }
        }));
    }

    public void ConfigureCertificationEndpoints()
    {
        ConfigureSalesSuccessForAll();
        ConfigureInitialInventorySuccess();
        _handler.SetResponder(async (request, body) =>
        {
            var path = GetPath(request);

            if (path.Contains("activate-terminal", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse(HttpStatusCode.OK, new
                {
                    statusCode = 1,
                    remark = "Activated",
                    data = new
                    {
                        activatedTerminal = new
                        {
                            terminalId = "TERM-CERT-001",
                            terminalCredentials = new
                            {
                                jwtToken = "Bearer cert-jwt",
                                secretKey = "ART-Integration-Test-Secret-Key"
                            }
                        }
                    }
                });
            }

            if (path.Contains("terminal-activated-confirmation", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse(HttpStatusCode.OK, new
                {
                    statusCode = 1,
                    remark = "Confirmed",
                    data = new { }
                });
            }

            if (path.Contains("get-latest-configs", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse(HttpStatusCode.OK, new
                {
                    statusCode = 1,
                    remark = "Configs",
                    data = new
                    {
                        globalConfiguration = new { versionNo = 1, taxRates = new[] { new { id = "A", name = "VAT-A", rate = 17.5 } } },
                        terminalConfiguration = new
                        {
                            versionNo = 1,
                            tradingName = "Sandbox Terminal",
                            terminalSite = new { siteId = "SITE-01", siteName = "Site 01" }
                        },
                        taxpayerConfiguration = new
                        {
                            versionNo = 1,
                            tin = "20162939",
                            activatedTaxRateIds = new[] { "A", "E" }
                        }
                    }
                });
            }

            if (path.Contains("process-credit-debit-note", StringComparison.OrdinalIgnoreCase))
            {
                return CreateSuccessSalesResponse("CERT-CDN-001", "FSIG-CDN-001");
            }

            if (path.Contains("submit-sales-transaction", StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeSalesResponderAsync(body, request).ConfigureAwait(false);
            }

            if (path.Contains("upload-initial-inventory", StringComparison.OrdinalIgnoreCase))
            {
                return await _inventoryResponder(request, body).ConfigureAwait(false);
            }

            return CreateJsonResponse(HttpStatusCode.NotFound, new { statusCode = 0, remark = "Unhandled mock route" });
        });
    }

    private async Task<HttpResponseMessage> ResolveResponseAsync(HttpRequestMessage request, string? body)
    {
        var path = GetPath(request);

        if (path.Contains("submit-sales-transaction", StringComparison.OrdinalIgnoreCase))
        {
            return await InvokeSalesResponderAsync(body, request).ConfigureAwait(false);
        }

        if (path.Contains("last-submitted-online-transaction", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("last-submitted-offline-transaction", StringComparison.OrdinalIgnoreCase))
        {
            return CreateJsonResponse(HttpStatusCode.OK, new
            {
                statusCode = 1,
                remark = "Last submitted invoice",
                data = new
                {
                    invoiceHeader = new
                    {
                        invoiceNumber = "BM6l7-B-B-B",
                        invoiceDateTime = DateTime.UtcNow,
                        sellerTIN = "20162939",
                        siteId = "SITE-01",
                        globalConfigVersion = 1,
                        taxpayerConfigVersion = 1,
                        terminalConfigVersion = 1,
                        isReliefSupply = false,
                        paymentMethod = "Cash"
                    },
                    invoiceLineItems = Array.Empty<object>(),
                    invoiceSummary = new
                    {
                        taxBreakDown = Array.Empty<object>(),
                        totalVAT = 0m,
                        invoiceTotal = 0m,
                        amountTendered = 0m
                    },
                    dateSubmitted = DateTime.UtcNow
                }
            });
        }

        if (path.Contains("get-terminal-site-products", StringComparison.OrdinalIgnoreCase))
        {
            return CreateJsonResponse(HttpStatusCode.OK, new
            {
                statusCode = 1,
                remark = "Success",
                data = new object[]
                {
                    new
                    {
                        productCode = "1234567890123",
                        productName = "Coca Cola 500ml",
                        description = "Carbonated soft drink",
                        quantity = 120m,
                        unitOfMeasure = "Bottle",
                        price = 1500m,
                        siteId = "SITE-001",
                        productExpiryDate = "2025-12-31T00:00:00.000Z",
                        minimumStockLevel = 10m,
                        taxRateId = "A",
                        isProduct = true
                    },
                    new
                    {
                        productCode = "SRV001",
                        productName = "Car Wash Service",
                        description = "Standard car wash",
                        quantity = 0m,
                        unitOfMeasure = "Service",
                        price = 10000m,
                        siteId = "SITE-001",
                        productExpiryDate = (string?)null,
                        minimumStockLevel = 0m,
                        taxRateId = "E",
                        isProduct = false
                    }
                },
                errors = Array.Empty<object>()
            });
        }

        if (path.Contains("validate-vat5-certificate", StringComparison.OrdinalIgnoreCase))
        {
            return CreateJsonResponse(HttpStatusCode.OK, new
            {
                statusCode = 1,
                remark = "VAT 5 certificate validation succeeded.",
                data = new
                {
                    projectNumber = "VATF/00000132/2024",
                    certificateNumber = "MRA/BMTO/VAT5/000169",
                    quantity = 80m,
                    dateOfIssue = "2024-02-23T00:00:00",
                    dateOfExpiry = "2099-03-24T00:00:00"
                },
                errors = (object?)null
            });
        }

        if (path.Contains("get-terminal-blocking-message", StringComparison.OrdinalIgnoreCase))
        {
            return CreateJsonResponse(HttpStatusCode.OK, new
            {
                statusCode = 1,
                remark = "Terminal blocking message retrieved.",
                data = new
                {
                    isBlocked = true,
                    blockingReason =
                        "Terminal blocked by MRA for compliance review. Contact MRA Taxpayer Services and stop all sales until the terminal is unblocked.",
                    blockedAt = "2025-05-28T06:42:59.980Z"
                },
                errors = (object?)null
            });
        }

        if (path.Contains("upload-initial-inventory", StringComparison.OrdinalIgnoreCase))
        {
            return await _inventoryResponder(request, body).ConfigureAwait(false);
        }

        return CreateJsonResponse(HttpStatusCode.NotFound, new { statusCode = 0, remark = "Unhandled mock route" });
    }

    private async Task<HttpResponseMessage> InvokeSalesResponderAsync(string? body, HttpRequestMessage request)
    {
        if (_rejectAllInvalidHmac && !ValidateHmacIfEnabled(request, body))
        {
            return CreateJsonResponse(HttpStatusCode.Unauthorized, new
            {
                statusCode = 0,
                remark = "x-signature HMAC does not match request body."
            });
        }

        Func<string, HttpRequestMessage?, Task<HttpResponseMessage>> responder;
        lock (_salesGate)
        {
            responder = _salesResponder;
        }

        return await responder(body ?? string.Empty, request).ConfigureAwait(false);
    }

    private bool ValidateHmacIfEnabled(HttpRequestMessage request, string? body)
    {
        if (string.IsNullOrWhiteSpace(_hmacSecretForVerification) || string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        if (!request.Headers.TryGetValues(HmacSignatureService.SignatureHeaderName, out var values))
        {
            return false;
        }

        var sent = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sent))
        {
            return false;
        }

        var expected = HmacSignatureService.ComputeHmacSha512Base64(body, _hmacSecretForVerification);
        return string.Equals(sent, expected, StringComparison.Ordinal);
    }

    private static string GetPath(HttpRequestMessage request) =>
        request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.AbsolutePath
            : request.RequestUri?.OriginalString ?? string.Empty;

    private static HttpResponseMessage CreateSuccessSalesResponse(
        string invoiceNumber,
        string fiscalSignature = "FSIG-SANDBOX-001",
        string verificationUrl = "https://dev-eis-portal.mra.mw/verify/test") =>
        CreateJsonResponse(HttpStatusCode.OK, new
        {
            statusCode = 1,
            remark = "Sale fiscalized",
            data = new
            {
                invoiceNumber,
                fiscalSignature,
                verificationUrl
            }
        });

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string ParseInvoiceNumber(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return "UNKNOWN";
        }

        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("invoiceHeader", out var header) &&
                header.TryGetProperty("invoiceNumber", out var number))
            {
                return number.GetString() ?? "UNKNOWN";
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return "UNKNOWN";
    }

    public void Dispose() => _handler.Dispose();
}

public sealed record RecordedMraEisRequest(
    string Method,
    string Path,
    string? Body,
    IReadOnlyDictionary<string, IEnumerable<string>> Headers);

internal sealed class MockMraHttpHandler : HttpMessageHandler
{
    private readonly List<RecordedMraEisRequest> _requests = new();
    private Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> _responder = (_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public IReadOnlyList<RecordedMraEisRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public void SetResponder(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var recorded = new RecordedMraEisRequest(
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            body,
            request.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase));

        lock (_requests)
        {
            _requests.Add(recorded);
        }

        return await _responder(request, body).ConfigureAwait(false);
    }
}
