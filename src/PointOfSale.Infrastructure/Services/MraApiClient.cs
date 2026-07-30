using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Http;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// MRA EIS HTTP client — Authorization JWT and optional x-signature (HMAC-SHA512, Base64).
/// Uses named <see cref="IHttpClientFactory"/> client <see cref="MraHttpClientFactory.ClientName"/>;
/// each send obtains a factory client and never mutates BaseAddress/Timeout/DefaultRequestHeaders.
/// </summary>
public sealed class MraApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MraApiOptions _options;
    private readonly ILogger<MraApiClient> _logger;
    private readonly IAuditLoggingService? _auditLoggingService;
    private readonly MraRuntimeEnvironmentState? _runtimeState;

    /// <summary>
    /// Production DI constructor. Marked so MS.DI does not also try the parameterless
    /// <see cref="HttpClient"/> activator (ambiguous constructors crash the sales UI).
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public MraApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<MraApiOptions> options,
        ILogger<MraApiClient> logger,
        IAuditLoggingService? auditLoggingService = null,
        MraRuntimeEnvironmentState? runtimeState = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _auditLoggingService = auditLoggingService;
        _runtimeState = runtimeState;
    }

    /// <summary>
    /// Test/harness factory — wraps a single <see cref="HttpClient"/> without mutating it.
    /// Prefer this over a second public constructor so DI activation stays unambiguous.
    /// </summary>
    public static MraApiClient CreateForTests(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger<MraApiClient> logger,
        IAuditLoggingService? auditLoggingService = null,
        MraRuntimeEnvironmentState? runtimeState = null)
    {
        // Ensure test harnesses that rely on HTTP timeouts (e.g. mocked delays)
        // behave consistently even when callers don't explicitly set HttpClient.Timeout.
        if (options.Value.HttpTimeout > TimeSpan.Zero)
        {
            httpClient.Timeout = options.Value.HttpTimeout;
        }

        return new(new FixedHttpClientFactory(httpClient), options, logger, auditLoggingService, runtimeState);
    }

    public async Task<EisApiResponse<TResponse>> PostAsync<TRequest, TResponse>(
        string relativePath,
        TRequest body,
        MraRequestContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body, MraJson.SerializerOptions);
        // Must await before disposing the request — returning SendAsync without await
        // disposed StringContent mid-flight ("Cannot access a disposed object").
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = CreateJsonContent(json)
        };

        ApplyContext(request, context, signaturePlainText: context?.SignaturePlainText ?? json, jsonBody: json);
        return await SendAsync<TResponse>(request, auditRequestBody: json, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<EisApiResponse<TResponse>> GetAsync<TResponse>(
        string relativePath,
        MraRequestContext? context = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<TResponse>(relativePath, query: null, context, cancellationToken);

    public async Task<EisApiResponse<TResponse>> GetAsync<TResponse>(
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
        return await SendAsync<TResponse>(request, auditRequestBody: auditBody, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Official MRA OpenAPI: <c>POST /api/v1/configuration/get-latest-configs</c>
    /// with Authorization JWT and an empty JSON body (no x-signature).
    /// Live EIS requires <c>Bearer</c> — raw JWT returns opaque HTTP 500 (same as ping/sales).
    /// </summary>
    public Task<EisApiResponse<TResponse>> GetLatestConfigsAsync<TResponse>(
        string jwtToken,
        CancellationToken cancellationToken = default) =>
        PostAsync<object, TResponse>(
            "configuration/get-latest-configs",
            new { },
            new MraRequestContext
            {
                JwtToken = jwtToken,
                UseBearerAuthorization = true
            },
            cancellationToken);

    public static string ComputeSignature(string plainText, string secretKey) =>
        HmacSignatureService.ComputeHmacSha512(plainText, secretKey);

    /// <summary>
    /// JSON body without <c>charset=utf-8</c> — some EIS gateways reject or mishandle charset on Content-Type.
    /// </summary>
    private static StringContent CreateJsonContent(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private void ApplyContext(HttpRequestMessage request, MraRequestContext? context, string? signaturePlainText, string? jsonBody)
    {
        if (context?.JwtToken is { Length: > 0 } jwt)
        {
            var normalized = MraJwtClaims.NormalizeAuthorizationToken(jwt);
            if (normalized.Length > 0)
            {
                var authorization = context.UseBearerAuthorization
                    ? $"Bearer {normalized}"
                    : normalized;
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }
        }

        if (!string.IsNullOrWhiteSpace(context?.AcceptHeader))
        {
            request.Headers.Remove("Accept");
            request.Headers.TryAddWithoutValidation("Accept", context.AcceptHeader.Trim());
        }

        if (context?.VendorAccessKey is { Length: > 0 } accessKey)
        {
            request.Headers.TryAddWithoutValidation(MraVendorAccessKeyPolicy.HeaderName, accessKey.Trim());
            _logger.LogDebug(
                "Attached {Header} for {Method} {Path}",
                MraVendorAccessKeyPolicy.HeaderName,
                request.Method,
                request.RequestUri);
        }

        // Confirmation / signed POSTs.
        if (context?.IsActivationConfirmationSignature == true)
        {
            // Confirmation: Bearer JWT + x-signature(HMAC-SHA512(TAC)). No x-eis-message-hash.
            if (!string.IsNullOrWhiteSpace(context.RawSignatureHeaderValue))
            {
                HmacSignatureService.ApplyXSignatureHeader(request, context.RawSignatureHeaderValue.Trim());
            }
            else if (context.SecretKey is { Length: > 0 } confirmSecret
                     && !string.IsNullOrWhiteSpace(signaturePlainText))
            {
                HmacSignatureService.AttachActivationConfirmationSignature(
                    request,
                    signaturePlainText,
                    confirmSecret.Trim());
            }

            _logger.LogInformation(
                "Confirmation headers for {Path}: Authorization={HasAuth}, x-signature={HasSig}, bearer={UseBearer}",
                request.RequestUri,
                !string.IsNullOrWhiteSpace(context.JwtToken),
                request.Headers.Contains(HmacSignatureService.SignatureHeaderName),
                context.UseBearerAuthorization);
        }
        else if (context?.SecretKey is { Length: > 0 } secretKey &&
            !string.IsNullOrWhiteSpace(signaturePlainText))
        {
            MraEisMessageHash.SetSecretKeyOption(request, secretKey);
            MraEisMessageHash.SetPlainTextOption(request, jsonBody ?? string.Empty);
            MraEisMessageHash.TryAttach(request, jsonBody ?? string.Empty, secretKey);
            HmacSignatureService.ApplyXSignatureHeader(
                request,
                ComputeSignature(signaturePlainText, secretKey));

            _logger.LogDebug(
                "Attached {Header} for {Method} {Path}",
                HmacSignatureService.SignatureHeaderName,
                request.Method,
                request.RequestUri);
        }
        else if (context?.SecretKey is { Length: > 0 } payloadSecret &&
                 !string.IsNullOrWhiteSpace(jsonBody))
        {
            MraEisMessageHash.SetSecretKeyOption(request, payloadSecret);
            MraEisMessageHash.SetPlainTextOption(request, jsonBody);
            MraEisMessageHash.TryAttach(request, jsonBody, payloadSecret);

            HmacSignatureService.ApplyXSignatureHeader(
                request,
                ComputeSignature(jsonBody, payloadSecret));
        }
        else if (context?.SecretKey is { Length: > 0 } hashSecret)
        {
            // JWT + secret without x-signature plaintext (e.g. empty-body last-submitted) still needs message hash.
            var payload = jsonBody ?? string.Empty;
            MraEisMessageHash.SetSecretKeyOption(request, hashSecret);
            MraEisMessageHash.SetPlainTextOption(request, payload);
            MraEisMessageHash.TryAttach(request, payload, hashSecret);
        }
    }

    /// <summary>
    /// POST with an empty body (<c>-d ''</c>), optional Accept header, JWT Authorization, no x-signature.
    /// Used by <c>utilities/ping</c> and <c>sales/last-submitted-*-transaction</c>.
    /// </summary>
    public async Task<EisApiResponse<TResponse>> PostEmptyAsync<TResponse>(
        string relativePath,
        MraRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            // curl -d '' with Content-Type: application/json (MRA ping / last-submitted samples)
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
        };

        ApplyContext(request, context, signaturePlainText: null, jsonBody: null);
        return await SendAsync<TResponse>(request, auditRequestBody: string.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EisApiResponse<TResponse>> SendAsync<TResponse>(
        HttpRequestMessage request,
        string? auditRequestBody,
        CancellationToken cancellationToken)
    {
        EnsureAbsoluteRequestUri(request);
        _logger.LogInformation("MRA EIS {Method} {Uri}", request.Method, request.RequestUri);

        if (!string.IsNullOrWhiteSpace(auditRequestBody) &&
            request.Method == HttpMethod.Post)
        {
            _logger.LogInformation(
                "MRA EIS request payload for {Uri}: {RequestPayload}",
                request.RequestUri,
                TruncateForLog(auditRequestBody, max: 16000));
        }

        var path = ResolveAuditPath(request.RequestUri);
        var started = Environment.TickCount64;

        // Fresh factory client per call — never mutate Timeout/BaseAddress/headers on a shared instance.
        var httpClient = _httpClientFactory.CreateClient(MraHttpClientFactory.ClientName);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

            if (!response.IsSuccessStatusCode)
            {
                LogFailedExchange(request, auditRequestBody, response.StatusCode, content);
            }

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
                // Non-JSON 500/HTML bodies are common on the sandbox gateway — keep raw body on the exception.
                if (!response.IsSuccessStatusCode)
                {
                    throw new MraApiException(
                        MraApiException.FormatHttpError((int)response.StatusCode, response.ReasonPhrase, content),
                        (int)response.StatusCode,
                        content,
                        ex);
                }

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
                    MraApiException.FormatHttpError((int)response.StatusCode, response.ReasonPhrase, content, parsed.Remark, parsed.Errors),
                    (int)response.StatusCode,
                    content);
            }

            if (!parsed.IsSuccess)
            {
                LogLogicalFailure(request, auditRequestBody, parsed.StatusCode, parsed.Remark, parsed.Errors, content);
            }

            return parsed;
        }
        catch (Exception ex) when (ex is not MraApiException)
        {
            var durationMs = (int)Math.Min(int.MaxValue, Environment.TickCount64 - started);
            var detail = FormatTransportError(ex, request.RequestUri);
            _logger.LogError(ex, "MRA EIS transport failure for {Uri}: {Detail}", request.RequestUri, detail);

            await AuditAsync(
                request.Method.Method,
                path,
                httpStatusCode: null,
                durationMs,
                auditRequestBody,
                responseBody: null,
                isSuccess: false,
                errorMessage: detail,
                cancellationToken).ConfigureAwait(false);

            throw new MraApiException(detail, httpStatusCode: 0, responseBody: null, inner: ex);
        }
    }

    private void LogFailedExchange(
        HttpRequestMessage request,
        string? requestBody,
        System.Net.HttpStatusCode statusCode,
        string? responseBody)
    {
        _logger.LogWarning(
            "MRA EIS HTTP {Status} for {Method} {Uri}. RequestPayload={RequestPayload}. ResponseBody={ResponseBody}",
            (int)statusCode,
            request.Method,
            request.RequestUri,
            TruncateForLog(requestBody, max: 32000),
            TruncateForLog(responseBody, max: 32000));
    }

    private void LogLogicalFailure(
        HttpRequestMessage request,
        string? requestBody,
        int statusCode,
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors,
        string? responseBody)
    {
        var errorsJson = errors is null || errors.Count == 0
            ? "(none)"
            : JsonSerializer.Serialize(errors, MraJson.SerializerOptions);

        _logger.LogWarning(
            "MRA EIS logical failure for {Method} {Uri}. statusCode={StatusCode}, remark={Remark}, errors={Errors}. RequestPayload={RequestPayload}. ResponseBody={ResponseBody}",
            request.Method,
            request.RequestUri,
            statusCode,
            remark ?? "(null)",
            errorsJson,
            TruncateForLog(requestBody, max: 32000),
            TruncateForLog(responseBody, max: 32000));
    }

    private static string TruncateForLog(string? value, int max = 4000)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= max ? value : value[..max] + "…(truncated)";
    }

    private static string FormatTransportError(Exception ex, Uri? requestUri)
    {
        var root = ex;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        var target = requestUri?.ToString() ?? "(unknown uri)";
        return $"MRA EIS request failed for {target}: {root.GetType().Name}: {root.Message}";
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

    private static string ResolveAuditPath(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return "(unknown)";
        }

        if (requestUri.IsAbsoluteUri)
        {
            return requestUri.PathAndQuery;
        }

        return requestUri.OriginalString;
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

    /// <summary>
    /// Builds an absolute RequestUri per call. Never mutates <see cref="HttpClient.BaseAddress"/>.
    /// </summary>
    private void EnsureAbsoluteRequestUri(HttpRequestMessage request)
    {
        var baseUrl = _runtimeState?.GetEffectiveBaseUrl(_options) ?? _options.ResolveBaseUrl();
        baseUrl = MraApiOptions.NormalizeBaseUrl(baseUrl);

        if (request.RequestUri is { IsAbsoluteUri: true } absolute)
        {
            // Rewrite legacy unreachable absolute URIs that may have been baked into callers.
            if (MraApiOptions.IsLegacyUnreachableHost(absolute.ToString()))
            {
                var relative = absolute.AbsolutePath.TrimStart('/') + absolute.Query;
                // Drop a duplicated api/v1 prefix if the absolute URI already contained it.
                if (relative.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative["api/v1/".Length..];
                }

                request.RequestUri = MraApiOptions.CombineEndpoint(baseUrl, relative);
            }

            return;
        }

        var relativePath = request.RequestUri?.ToString() ?? string.Empty;
        request.RequestUri = MraApiOptions.CombineEndpoint(baseUrl, relativePath);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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

    /// <summary>
    /// When true, <see cref="SignaturePlainText"/> is treated as the TAC for
    /// <c>POST onboarding/terminal-activated-confirmation</c> (HMAC-SHA512 → Base64 x-signature).
    /// </summary>
    public bool IsActivationConfirmationSignature { get; init; }

    /// <summary>
    /// When set with <see cref="IsActivationConfirmationSignature"/>, sent as <c>x-signature</c>
    /// without HMAC (used to match MRA sample curls that put the JWT in that header).
    /// </summary>
    public string? RawSignatureHeaderValue { get; init; }

    /// <summary>
    /// Production-only vendor access key for <c>onboarding/activate-terminal</c>
    /// (<c>x-access-key</c> header). Never set for sandbox calls.
    /// </summary>
    public string? VendorAccessKey { get; init; }

    /// <summary>
    /// When true, Authorization is sent as <c>Bearer {jwt}</c>.
    /// Default false preserves historical MRA raw-token samples used by most EIS endpoints.
    /// </summary>
    public bool UseBearerAuthorization { get; init; }

    /// <summary>
    /// Optional HTTP <c>Accept</c> header (e.g. <c>text/plain</c> for last-submitted-* queries).
    /// </summary>
    public string? AcceptHeader { get; init; }
}
