using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// MRA EIS HTTP client — Authorization JWT and optional x-signature (HMAC-SHA512, Base64).
/// </summary>
public sealed class MraApiClient
{
    private readonly HttpClient _httpClient;
    private readonly MraApiOptions _options;
    private readonly ILogger<MraApiClient> _logger;
    private readonly IAuditLoggingService? _auditLoggingService;

    public MraApiClient(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger<MraApiClient> logger,
        IAuditLoggingService? auditLoggingService = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _auditLoggingService = auditLoggingService;

        var baseUrl = _options.ResolveBaseUrl();
        if (_httpClient.BaseAddress is null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            _httpClient.BaseAddress = baseUri;
        }

        _httpClient.Timeout = _options.HttpTimeout;
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
    }

    public Task<EisApiResponse<TResponse>> PostAsync<TRequest, TResponse>(
        string relativePath,
        TRequest body,
        MraRequestContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body, MraJson.SerializerOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyContext(request, context, signaturePlainText: context?.SignaturePlainText ?? json, jsonBody: json);
        return SendAsync<TResponse>(request, auditRequestBody: json, cancellationToken);
    }

    public Task<EisApiResponse<TResponse>> GetAsync<TResponse>(
        string relativePath,
        MraRequestContext? context = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<TResponse>(relativePath, query: null, context, cancellationToken);

    public Task<EisApiResponse<TResponse>> GetAsync<TResponse>(
        string relativePath,
        IReadOnlyDictionary<string, string>? query,
        MraRequestContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var path = BuildPathWithQuery(relativePath, query);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApplyContext(request, context, signaturePlainText: context?.SignaturePlainText, jsonBody: null);
        var auditBody = query is null || query.Count == 0
            ? null
            : JsonSerializer.Serialize(query, MraJson.SerializerOptions);
        return SendAsync<TResponse>(request, auditRequestBody: auditBody, cancellationToken);
    }

    /// <summary>
    /// MRA documents get-latest-configs as HTTP GET with Authorization JWT.
    /// </summary>
    public Task<EisApiResponse<TResponse>> GetLatestConfigsAsync<TResponse>(
        string jwtToken,
        CancellationToken cancellationToken = default) =>
        GetAsync<TResponse>("configuration/get-latest-configs", new MraRequestContext { JwtToken = jwtToken }, cancellationToken);

    public static string ComputeSignature(string plainText, string secretKey) =>
        HmacSignatureService.ComputeHmacSha512Base64(plainText, secretKey);

    private void ApplyContext(HttpRequestMessage request, MraRequestContext? context, string? signaturePlainText, string? jsonBody)
    {
        if (context?.JwtToken is { Length: > 0 } jwt)
        {
            request.Headers.TryAddWithoutValidation("Authorization", jwt.Trim());
        }

        if (context?.SecretKey is { Length: > 0 } secretKey &&
            !string.IsNullOrWhiteSpace(signaturePlainText))
        {
            var signature = ComputeSignature(signaturePlainText, secretKey);
            request.Headers.TryAddWithoutValidation(HmacSignatureService.SignatureHeaderName, signature);
        }
        else if (context?.SecretKey is { Length: > 0 } payloadSecret &&
                 !string.IsNullOrWhiteSpace(jsonBody))
        {
            var signature = ComputeSignature(jsonBody, payloadSecret);
            request.Headers.TryAddWithoutValidation(HmacSignatureService.SignatureHeaderName, signature);
        }
    }

    private async Task<EisApiResponse<TResponse>> SendAsync<TResponse>(
        HttpRequestMessage request,
        string? auditRequestBody,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("MRA EIS {Method} {Uri}", request.Method, request.RequestUri);

        var path = request.RequestUri?.PathAndQuery ?? request.RequestUri?.ToString() ?? "(unknown)";
        var started = Environment.TickCount64;

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var durationMs = (int)Math.Min(int.MaxValue, Environment.TickCount64 - started);

            await AuditAsync(
                request.Method.Method,
                path,
                (int)response.StatusCode,
                durationMs,
                auditRequestBody,
                content,
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? null : content,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new MraApiException(
                    $"Empty MRA EIS response ({(int)response.StatusCode}).",
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
                    "Invalid MRA EIS response JSON.",
                    (int)response.StatusCode,
                    content,
                    ex);
            }

            if (parsed is null)
            {
                throw new MraApiException(
                    "MRA EIS response was null.",
                    (int)response.StatusCode,
                    content);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MraApiException(
                    $"MRA EIS HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    (int)response.StatusCode,
                    content);
            }

            return parsed;
        }
        catch (Exception ex) when (ex is not MraApiException)
        {
            var durationMs = (int)Math.Min(int.MaxValue, Environment.TickCount64 - started);
            await AuditAsync(
                request.Method.Method,
                path,
                httpStatusCode: null,
                durationMs,
                auditRequestBody,
                responseBody: null,
                isSuccess: false,
                errorMessage: ex.Message,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Task AuditAsync(
        string method,
        string path,
        int? httpStatusCode,
        int durationMs,
        string? requestBody,
        string? responseBody,
        bool isSuccess,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (_auditLoggingService is null)
        {
            return Task.CompletedTask;
        }

        return _auditLoggingService.LogMraExchangeAsync(
            method,
            path,
            httpStatusCode,
            durationMs,
            requestBody,
            responseBody,
            isSuccess,
            errorMessage,
            cancellationToken);
    }

    private static string BuildPathWithQuery(string relativePath, IReadOnlyDictionary<string, string>? query)
    {
        if (query is null || query.Count == 0)
        {
            return relativePath;
        }

        var qs = string.Join(
            "&",
            query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        return relativePath.Contains('?', StringComparison.Ordinal)
            ? $"{relativePath}&{qs}"
            : $"{relativePath}?{qs}";
    }
}

public sealed class MraRequestContext
{
    public string? JwtToken { get; init; }
    public string? SecretKey { get; init; }

    /// <summary>
    /// When set, HMAC is computed over this text (e.g. Terminal Activation Code for onboarding confirmation).
    /// When null and SecretKey is set with a POST body, HMAC is computed over the JSON payload.
    /// </summary>
    public string? SignaturePlainText { get; init; }
}
