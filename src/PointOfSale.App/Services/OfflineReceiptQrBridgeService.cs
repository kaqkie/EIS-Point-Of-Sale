using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Security;

namespace PointOfSale.App.Services;

public interface IOfflineReceiptQrBridge
{
    bool IsListening { get; }

    /// <summary>
    /// Rewrites an offline MRA ValidationURL to this till's LAN bridge so phone scans
    /// do not hit the broken MRA portal page. Online EIS validation URLs are left unchanged.
    /// </summary>
    string? RewriteForScan(string? validationUrl);
}

/// <summary>
/// Serves a local HMAC verification page for offline receipt QR codes on the store LAN.
/// </summary>
public sealed class OfflineReceiptQrBridgeService : BackgroundService, IOfflineReceiptQrBridge
{
    private readonly OfflineReceiptQrBridgeOptions _options;
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly ILogger<OfflineReceiptQrBridgeService> _logger;
    private HttpListener? _listener;
    private string? _publicValidateBaseUrl;

    public OfflineReceiptQrBridgeService(
        IOptions<OfflineReceiptQrBridgeOptions> options,
        IMraTerminalAuthProvider authProvider,
        ILogger<OfflineReceiptQrBridgeService> logger)
    {
        _options = options.Value;
        _authProvider = authProvider;
        _logger = logger;
    }

    public bool IsListening => _listener?.IsListening == true && !string.IsNullOrWhiteSpace(_publicValidateBaseUrl);

    public string? RewriteForScan(string? validationUrl)
    {
        if (!_options.Enabled
            || string.IsNullOrWhiteSpace(validationUrl)
            || !IsListening
            || string.IsNullOrWhiteSpace(_publicValidateBaseUrl))
        {
            return validationUrl;
        }

        if (!MraReceiptLayoutService.IsOfflineValidationUrl(validationUrl))
        {
            return validationUrl;
        }

        if (!Uri.TryCreate(validationUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return validationUrl;
        }

        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return validationUrl;
        }

        return _publicValidateBaseUrl.TrimEnd('/') + "/" + query;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Offline receipt QR bridge disabled.");
            return;
        }

        var port = _options.Port <= 0 ? 18787 : _options.Port;
        var lanIp = TryGetLanIPv4();
        if (string.IsNullOrWhiteSpace(lanIp))
        {
            _logger.LogWarning("Offline receipt QR bridge: no LAN IPv4 found; bridge not started.");
            return;
        }

        var path = NormalizePath(_options.Path);
        var prefix = $"http://{lanIp}:{port}{path}";
        _publicValidateBaseUrl = prefix;

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);

        try
        {
            _listener.Start();
            _logger.LogInformation(
                "Offline receipt QR bridge listening at {Prefix} (phone must be on same Wi‑Fi as the till).",
                prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start offline receipt QR bridge on {Prefix}. QR scans may still hit MRA portal ISE.",
                prefix);
            _listener = null;
            _publicValidateBaseUrl = null;
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var contextTask = _listener.GetContextAsync();
                var completed = await Task.WhenAny(
                        contextTask,
                        Task.Delay(Timeout.Infinite, stoppingToken))
                    .ConfigureAwait(false);
                if (completed != contextTask)
                {
                    break;
                }

                var context = await contextTask.ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected shutdown
        }
        catch (HttpListenerException ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Offline receipt QR bridge listener stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offline receipt QR bridge listener faulted.");
        }
        finally
        {
            TryStopListener();
        }
    }

    public override void Dispose()
    {
        TryStopListener();
        base.Dispose();
    }

    private void TryStopListener()
    {
        try
        {
            if (_listener is { IsListening: true })
            {
                _listener.Stop();
            }

            _listener?.Close();
        }
        catch
        {
            // ignore shutdown races
        }
        finally
        {
            _listener = null;
            _publicValidateBaseUrl = null;
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var query = context.Request.Url?.Query ?? string.Empty;
            var html = await BuildHtmlAsync(query).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline receipt QR bridge failed to serve a request.");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<string> BuildHtmlAsync(string query)
    {
        var parsed = ParseQuery(query);
        parsed.TryGetValue("TI", out var ti);
        parsed.TryGetValue("N", out var n);
        parsed.TryGetValue("I", out var i);
        parsed.TryGetValue("V", out var v);
        parsed.TryGetValue("T", out var t);
        parsed.TryGetValue("S", out var s);
        ti = RestorePlus(ti ?? string.Empty);
        t = RestorePlus(t ?? string.Empty);
        s = RestorePlus(s ?? string.Empty);
        n ??= string.Empty;
        i ??= string.Empty;
        v ??= string.Empty;

        var param = $"TI={ti}&N={n}&I={i}&V={v}&T={t}";
        var signatureOk = false;
        var signatureDetail = "Terminal secret unavailable — cannot verify HMAC on this till.";

        try
        {
            var ctx = await _authProvider.GetSignedContextAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ctx.SecretKey) && !string.IsNullOrWhiteSpace(s))
            {
                var expected = HmacSignatureService.ComputeHmacWithSha256(param, ctx.SecretKey);
                var left = Encoding.UTF8.GetBytes(expected);
                var right = Encoding.UTF8.GetBytes(s.Trim());
                signatureOk = left.Length == right.Length
                    && CryptographicOperations.FixedTimeEquals(left, right);
                signatureDetail = signatureOk
                    ? "Offline HMAC signature is valid for this terminal."
                    : "Offline HMAC signature does not match this terminal secret.";
            }
        }
        catch (Exception ex)
        {
            signatureDetail = "Could not load terminal secret: " + ex.Message;
        }

        var statusColor = signatureOk ? "#0a7a32" : "#b00020";
        var statusText = signatureOk ? "VALID OFFLINE RECEIPT" : "SIGNATURE CHECK FAILED";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Albert Retail — Offline receipt check</title>
              <style>
                body { font-family: Segoe UI, sans-serif; margin: 1.5rem; color: #1a1a1a; background: #f7f7f5; }
                .card { max-width: 32rem; margin: 0 auto; background: #fff; border: 1px solid #ddd; border-radius: 8px; padding: 1.25rem; }
                h1 { font-size: 1.15rem; margin: 0 0 .5rem; color: {{statusColor}}; }
                .muted { color: #555; font-size: .9rem; line-height: 1.4; }
                dl { margin: 1rem 0 0; }
                dt { font-size: .75rem; text-transform: uppercase; color: #777; margin-top: .65rem; }
                dd { margin: .15rem 0 0; font-family: Consolas, monospace; word-break: break-all; }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>{{WebUtility.HtmlEncode(statusText)}}</h1>
                <p class="muted">{{WebUtility.HtmlEncode(signatureDetail)}}</p>
                <p class="muted">
                  MRA's public ReceiptValidation portal is currently returning Internal Server Error.
                  This till verifies the same offline ValidationURL HMAC locally. After MRA activates
                  VAT sales and the invoice syncs online, scan the EIS portal QR from a newly printed receipt.
                </p>
                <dl>
                  <dt>Invoice (TI)</dt><dd>{{WebUtility.HtmlEncode(ti)}}</dd>
                  <dt>Items (N)</dt><dd>{{WebUtility.HtmlEncode(n)}}</dd>
                  <dt>Total (I)</dt><dd>{{WebUtility.HtmlEncode(i)}}</dd>
                  <dt>VAT (V)</dt><dd>{{WebUtility.HtmlEncode(v)}}</dd>
                  <dt>Julian (T)</dt><dd>{{WebUtility.HtmlEncode(t)}}</dd>
                </dl>
              </div>
            </body>
            </html>
            """;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raw = query.StartsWith('?') ? query[1..] : query;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string RestorePlus(string value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace(' ', '+');

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/ReceiptValidation/Validate/";
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    private static string? TryGetLanIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var props = ni.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return null;
    }
}
