using System.Net.Http.Headers;
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
    private readonly MraApiOptions _options;
    private readonly ILogger _logger;

    protected MraApiClientBase(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null && Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            _httpClient.BaseAddress = baseUri;
        }

        _httpClient.Timeout = _options.HttpTimeout;
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
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body, MraJson.SerializerOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthorization(request, jwtToken);

        if (!string.IsNullOrWhiteSpace(xSignaturePlainText) &&
            !string.IsNullOrWhiteSpace(secretKeyForSignature))
        {
            var signature = HmacSignatureService.ComputeHmacSha512Base64(
                xSignaturePlainText,
                secretKeyForSignature);
            request.Headers.TryAddWithoutValidation(HmacSignatureService.SignatureHeaderName, signature);
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
        request.Headers.TryAddWithoutValidation(
            HmacSignatureService.SignatureHeaderName,
            HmacSignatureService.ComputeHmacSha512Base64(json, secretKey));

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
                $"HTTP error calling MRA EIS: {response.ReasonPhrase}",
                (int)response.StatusCode,
                content);
        }

        return parsed;
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string? jwtToken)
    {
        if (string.IsNullOrWhiteSpace(jwtToken))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken.Trim());
        // MRA samples pass the raw JWT in Authorization without the Bearer prefix.
        if (!jwtToken.Contains(' ', StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("Authorization", jwtToken.Trim());
            request.Headers.Authorization = null;
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
}
