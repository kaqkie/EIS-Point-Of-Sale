using System.Net;
using System.Text;
using System.Text.Json;

namespace PointOfSale.Tests.Mocks;

/// <summary>
/// Lightweight in-process HTTP mock for MRA EIS sandbox responses (success, HTTP 400, delayed timeout).
/// </summary>
public sealed class MockMraServer : IDisposable
{
    private readonly MockMraHttpHandler _handler = new();
    private readonly object _salesGate = new();
    private Func<string, Task<HttpResponseMessage>> _salesResponder = body =>
        Task.FromResult(CreateSuccessSalesResponse(ParseInvoiceNumber(body)));

    private Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> _inventoryResponder = (_, _) =>
        Task.FromResult(CreateJsonResponse(HttpStatusCode.NotFound, new { statusCode = 0, remark = "Inventory route not configured." }));

    public MockMraServer()
    {
        _handler.SetResponder(ResolveResponseAsync);
        ConfigureSalesSuccessForAll();
    }

    public string BaseUrl => "https://mock-mra-eis.local/api/v1/";

    public HttpMessageHandler HttpHandler => _handler;

    public IReadOnlyList<RecordedMraRequest> SalesRequests =>
        _handler.Requests
            .Where(x => x.Path.Contains("submit-sales-transaction", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<RecordedMraRequest> InitialInventoryRequests =>
        _handler.Requests
            .Where(x => x.Path.Contains("upload-initial-inventory", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public void ResetSalesBehavior() =>
        SetSalesResponder(body => Task.FromResult(CreateSuccessSalesResponse(ParseInvoiceNumber(body))));

    public void SetSalesResponder(Func<string, Task<HttpResponseMessage>> responder)
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
        SetSalesResponder(body =>
        {
            var invoice = ParseInvoiceNumber(body);
            return Task.FromResult(CreateSuccessSalesResponse(invoice, fiscalSignature, verificationUrl));
        });
    }

    public void ConfigureSalesHttp400ForInvoice(string invoiceNumber, string remark = "MRA validation failure (simulated)")
    {
        SetSalesResponder(body =>
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

    public void ConfigureSalesTimeout(TimeSpan delay)
    {
        SetSalesResponder(async _ =>
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

    private async Task<HttpResponseMessage> ResolveResponseAsync(HttpRequestMessage request, string? body)
    {
        var path = request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.AbsolutePath
            : request.RequestUri?.OriginalString ?? string.Empty;

        if (path.Contains("submit-sales-transaction", StringComparison.OrdinalIgnoreCase))
        {
            Func<string, Task<HttpResponseMessage>> responder;
            lock (_salesGate)
            {
                responder = _salesResponder;
            }

            return await responder(body ?? string.Empty).ConfigureAwait(false);
        }

        if (path.Contains("upload-initial-inventory", StringComparison.OrdinalIgnoreCase))
        {
            return await _inventoryResponder(request, body).ConfigureAwait(false);
        }

        return CreateJsonResponse(HttpStatusCode.NotFound, new { statusCode = 0, remark = "Unhandled mock route" });
    }

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

public sealed record RecordedMraRequest(
    string Method,
    string Path,
    string? Body,
    IReadOnlyDictionary<string, IEnumerable<string>> Headers);

internal sealed class MockMraHttpHandler : HttpMessageHandler
{
    private readonly List<RecordedMraRequest> _requests = new();
    private Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> _responder = (_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public IReadOnlyList<RecordedMraRequest> Requests
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

        var recorded = new RecordedMraRequest(
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
