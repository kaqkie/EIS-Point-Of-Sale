using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Mra.Http;

public abstract class MraApiClientBase
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    protected MraApiClientBase(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger logger)
    {
        _httpClient = httpClient;
        _ = options.Value; // configured via IHttpClientFactory — do not mutate HttpClient here
        _logger = logger;
    }

    protected async Task<EisApiResponse<TResponse>> GetAsync<TResponse>(
        string relativePath,
        string? jwtToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        ApplyAuthorization(request, jwtToken);

        return await SendAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<EisApiResponse<TResponse>> PostJsonAsync<TRequest, TResponse>(
        string relativePath,
        TRequest body,
        string? jwtToken = null,
        string? xSignaturePlainText = null,
        string? secretKeyForSignature = null,
        string? vendorAccessKey = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body, MraJson.SerializerOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthorization(request, jwtToken);

        if (!string.IsNullOrWhiteSpace(vendorAccessKey))
        {
            request.Headers.TryAddWithoutValidation(
                Security.MraVendorAccessKeyPolicy.HeaderName,
                vendorAccessKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(xSignaturePlainText) &&
            !string.IsNullOrWhiteSpace(secretKeyForSignature))
        {
            var isConfirmation = relativePath.Contains(
                "terminal-activated-confirmation",
                StringComparison.OrdinalIgnoreCase);

            // Confirmation: JWT + x-signature(TAC) only. Other signed routes also get message hash.
            if (!isConfirmation)
            {
                MraEisMessageHash.TryAttach(request, json, secretKeyForSignature);
            }

            HmacSignatureService.AttachActivationConfirmationSignature(
                request,
                xSignaturePlainText,
                secretKeyForSignature);
        }

        return await SendAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<EisApiResponse<TResponse>> PostSignedPayloadAsync<TRequest, TResponse>(
        string relativePath,
        TRequest body,
        string secretKey,
        string? jwtToken,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body, MraJson.SerializerOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthorization(request, jwtToken);
        MraEisMessageHash.TryAttach(request, json, secretKey);
        HmacSignatureService.ApplyXSignatureHeader(
            request,
            HmacSignatureService.ComputeHmacSha512(json, secretKey));

        return await SendAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EisApiResponse<TResponse>> SendAsync<TResponse>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("MRA EIS {Method} {Uri}", request.Method, request.RequestUri);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new MraApiException(
                $"Empty response from MRA EIS ({(int)response.StatusCode} {response.ReasonPhrase}).",
                (int)response.StatusCode,
                content);
        }

        EisApiResponse<TResponse>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EisApiResponse<TResponse>>(content, MraJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new MraApiException(
                "Failed to deserialize MRA EIS response.",
                (int)response.StatusCode,
                content,
                ex);
        }

        if (parsed is null)
        {
            throw new MraApiException(
                "MRA EIS response deserialized to null.",
                (int)response.StatusCode,
                content);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MraApiException(
                MraApiException.FormatHttpError(
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    content,
                    parsed.Remark,
                    parsed.Errors),
                (int)response.StatusCode,
                content);
        }

        if (!parsed.IsSuccess)
        {
            _logger.LogWarning(
                "MRA EIS logical failure for {Method} {Uri}. statusCode={StatusCode}, remark={Remark}, errors={Errors}. ResponseBody={ResponseBody}",
                request.Method,
                request.RequestUri,
                parsed.StatusCode,
                parsed.Remark ?? "(null)",
                FormatErrorsForLog(parsed.Errors),
                TruncateForLog(content));
        }

        return parsed;
    }

    private static string FormatErrorsForLog(IReadOnlyList<EisApiError>? errors) =>
        errors is null || errors.Count == 0
            ? "(none)"
            : JsonSerializer.Serialize(errors, MraJson.SerializerOptions);

    private static string TruncateForLog(string? value, int max = 4000)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= max ? value : value[..max] + "…(truncated)";
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string? jwtToken)
    {
        if (string.IsNullOrWhiteSpace(jwtToken))
        {
            return;
        }

        // MRA samples pass the raw JWT in Authorization without the Bearer prefix.
        var normalized = Security.MraJwtClaims.NormalizeAuthorizationToken(jwtToken);
        if (normalized.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", normalized);
        }
    }
}

public sealed class MraApiException : Exception
{
    public MraApiException(string message, int httpStatusCode, string? responseBody, Exception? inner = null)
        : base(message, inner)
    {
        HttpStatusCode = httpStatusCode;
        ResponseBody = responseBody;
    }

    public int HttpStatusCode { get; }
    public string? ResponseBody { get; }

    /// <summary>
    /// Builds an operator/queue-facing message that includes EIS validation details from the response body
    /// (sandbox often returns HTTP 500 with a JSON <c>errors</c> / <c>remark</c> payload).
    /// </summary>
    public static string FormatHttpError(
        int statusCode,
        string? reasonPhrase,
        string? responseBody,
        string? remark = null,
        IReadOnlyList<EisApiError>? errors = null)
    {
        var summary = $"MRA EIS HTTP {statusCode}: {reasonPhrase ?? "error"}";
        var detail = BuildBodyDetail(responseBody, remark, errors);
        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} — {detail}";
    }

    /// <summary>
    /// True when the HTTP failure looks like a permanent payload/validation rejection
    /// (including sandbox 500s that embed field validation in the body).
    /// Opaque ASP.NET <c>{"message":"An internal error occurred"}</c> is treated as transient —
    /// MRA sandbox often returns that for host/auth outages, so the queue should retry with backoff.
    /// </summary>
    public bool LooksLikeValidationOrClientError()
    {
        if (IsHttpClientLifetimeError())
        {
            return true;
        }

        if (HttpStatusCode is >= 400 and < 500 and not 408 and not 429)
        {
            return true;
        }

        if (HttpStatusCode is < 500)
        {
            return false;
        }

        // Real field validation embedded in a 500 body → permanent. Opaque internal errors → retry.
        return HasValidationSignals(ResponseBody);
    }

    /// <summary>
    /// Detects the classic shared-HttpClient mutation fault so the queue can quarantine
    /// instead of retrying forever.
    /// </summary>
    public bool IsHttpClientLifetimeError()
    {
        for (var current = (Exception?)this; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                current.Message.Contains("already started one or more requests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sandbox often returns HTTP 500 with only <c>{"message":"An internal error occurred"}</c>
    /// when payload/schema/auth state is wrong — not a transient gateway blip.
    /// </summary>
    public static bool IsOpaqueSandboxInternalError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                return !string.IsNullOrWhiteSpace(text)
                    && text.Contains("internal error occurred", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return responseBody.Contains("An internal error occurred", StringComparison.OrdinalIgnoreCase)
            && !HasValidationSignals(responseBody);
    }

    public static bool HasValidationSignals(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                return true;
            }

            if (root.TryGetProperty("remark", out var remark) &&
                remark.ValueKind == JsonValueKind.String &&
                LooksLikeValidationText(remark.GetString()))
            {
                return true;
            }

            if (root.TryGetProperty("title", out var title) &&
                LooksLikeValidationText(title.GetString()))
            {
                return true;
            }

            if (root.TryGetProperty("detail", out var detail) &&
                LooksLikeValidationText(detail.GetString()))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw-text heuristics.
        }

        return LooksLikeValidationText(responseBody);
    }

    private static string? BuildBodyDetail(
        string? responseBody,
        string? remark,
        IReadOnlyList<EisApiError>? errors)
    {
        if (errors is { Count: > 0 })
        {
            var parts = errors
                .Select(e =>
                {
                    var field = string.IsNullOrWhiteSpace(e.FieldName) ? null : e.FieldName.Trim();
                    var msg = string.IsNullOrWhiteSpace(e.ErrorMessage) ? $"code {e.ErrorCode}" : e.ErrorMessage.Trim();
                    return field is null ? msg : $"{field}: {msg}";
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(8);
            var joined = string.Join("; ", parts);
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return string.IsNullOrWhiteSpace(remark) ? joined : $"{remark.Trim()} | {joined}";
            }
        }

        if (!string.IsNullOrWhiteSpace(remark))
        {
            return remark.Trim();
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            string? parsedRemark = null;
            if (root.TryGetProperty("remark", out var r) && r.ValueKind == JsonValueKind.String)
            {
                parsedRemark = NullIfWhiteSpace(r.GetString());
            }

            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                var pieces = new List<string>();
                foreach (var err in errs.EnumerateArray().Take(8))
                {
                    var field = err.TryGetProperty("fieldName", out var f) ? f.GetString() : null;
                    var msg = err.TryGetProperty("errorMessage", out var m) ? m.GetString() : err.ToString();
                    if (string.IsNullOrWhiteSpace(msg))
                    {
                        continue;
                    }

                    pieces.Add(string.IsNullOrWhiteSpace(field) ? msg.Trim() : $"{field.Trim()}: {msg.Trim()}");
                }

                if (pieces.Count > 0)
                {
                    var joined = string.Join("; ", pieces);
                    return string.IsNullOrWhiteSpace(parsedRemark) ? joined : $"{parsedRemark} | {joined}";
                }
            }

            if (!string.IsNullOrWhiteSpace(parsedRemark))
            {
                return parsedRemark;
            }

            if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var text = NullIfWhiteSpace(d.GetString());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var text = NullIfWhiteSpace(t.GetString());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var text = NullIfWhiteSpace(message.GetString());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // Use truncated raw body below.
        }

        var trimmed = responseBody.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool LooksLikeValidationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("validat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fieldName", StringComparison.OrdinalIgnoreCase)
            || text.Contains("errorCode", StringComparison.OrdinalIgnoreCase)
            || text.Contains("must be", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not match", StringComparison.OrdinalIgnoreCase)
            || text.Contains("bad request", StringComparison.OrdinalIgnoreCase);
    }
}
