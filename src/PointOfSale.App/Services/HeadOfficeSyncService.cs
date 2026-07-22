using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IHeadOfficeSyncService
{
    event EventHandler? StatusChanged;

    bool IsSyncing { get; }
    DateTime? LastSyncTimestamp { get; }
    int PendingUploadCount { get; }
    bool IsHeadOfficeReachable { get; }
    string ConnectionStatusText { get; }
    string? LastError { get; }

    Task<HeadOfficeSyncStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<HeadOfficeSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Packages branch sales summaries, closed-shift Z-reports, and inventory snapshots into
/// AES-GCM encrypted JSON, pushes deltas to head office when online, then pulls catalog updates.
/// Local offline-first POS operations are never blocked by sync failures.
/// </summary>
public sealed class HeadOfficeSyncService : IHeadOfficeSyncService
{
    public const string LastSyncConfigKey = "HeadOffice.LastSyncUtc";
    public const string LastCatalogConfigKey = "HeadOffice.LastCatalogPullUtc";
    public const string LastSalesCursorConfigKey = "HeadOffice.LastSalesCursorUtc";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HeadOfficeSyncOptions _options;
    private readonly TerminalDeploymentOptions _deployment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeadOfficeSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _isSyncing;
    private DateTime? _lastSyncTimestamp;
    private int _pendingUploadCount;
    private bool _isHeadOfficeReachable;
    private string _connectionStatusText = "Head office sync idle";
    private string? _lastError;

    public HeadOfficeSyncService(
        IOptions<HeadOfficeSyncOptions> options,
        IOptions<TerminalDeploymentOptions> deployment,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<HeadOfficeSyncService> logger)
    {
        _options = options.Value;
        _deployment = deployment.Value;
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public event EventHandler? StatusChanged;

    public bool IsSyncing => _isSyncing;
    public DateTime? LastSyncTimestamp => _lastSyncTimestamp;
    public int PendingUploadCount => _pendingUploadCount;
    public bool IsHeadOfficeReachable => _isHeadOfficeReachable;
    public string ConnectionStatusText => _connectionStatusText;
    public string? LastError => _lastError;

    public async Task<HeadOfficeSyncStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var outbox = sp.GetRequiredService<IHeadOfficeSyncOutboxRepository>();
        var config = sp.GetRequiredService<IConfigurationRepository>();

        var counts = await outbox.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        _pendingUploadCount = counts.Pending + counts.Failed;

        var lastSyncJson = await config.GetJsonAsync(LastSyncConfigKey, cancellationToken).ConfigureAwait(false);
        if (DateTime.TryParse(lastSyncJson, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastSync))
        {
            _lastSyncTimestamp = lastSync.ToUniversalTime();
        }

        var network = NetworkInterface.GetIsNetworkAvailable();
        if (_options.Enabled && network && TryCreateBaseUri(out var baseUri))
        {
            _isHeadOfficeReachable = await ProbeAsync(baseUri, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _isHeadOfficeReachable = false;
        }

        _connectionStatusText = BuildStatusText(network, counts.Failed);
        RaiseStatusChanged();

        var lastCatalogJson = await config.GetJsonAsync(LastCatalogConfigKey, cancellationToken).ConfigureAwait(false);
        DateTime? lastCatalog = null;
        if (DateTime.TryParse(lastCatalogJson, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedCatalog))
        {
            lastCatalog = parsedCatalog.ToUniversalTime();
        }

        return new HeadOfficeSyncStatusSnapshot
        {
            Enabled = _options.Enabled,
            IsSyncing = _isSyncing,
            IsHeadOfficeReachable = _isHeadOfficeReachable,
            IsNetworkAvailable = network,
            LastSyncTimestampUtc = _lastSyncTimestamp,
            LastCatalogPullUtc = lastCatalog,
            PendingUploadCount = _pendingUploadCount,
            FailedUploadCount = counts.Failed,
            ConnectionStatusText = _connectionStatusText,
            LastError = _lastError
        };
    }

    public async Task<HeadOfficeSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return HeadOfficeSyncResult.Disabled("Head-office sync is disabled in configuration.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new HeadOfficeSyncResult
            {
                Enabled = true,
                Success = true,
                Message = "Sync already in progress."
            };
        }

        _isSyncing = true;
        RaiseStatusChanged();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var outbox = sp.GetRequiredService<IHeadOfficeSyncOutboxRepository>();
            var shifts = sp.GetRequiredService<ICashierShiftRepository>();
            var inventory = sp.GetRequiredService<ILocalInventoryRepository>();
            var config = sp.GetRequiredService<IConfigurationRepository>();
            var terminals = sp.GetRequiredService<ITerminalRepository>();
            var connections = sp.GetRequiredService<ISqlConnectionFactory>();
            var catalog = sp.GetRequiredService<ICentralInventoryReplicationService>();

            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                await PackageLocalDeltasAsync(outbox, shifts, inventory, config, connections, cancellationToken)
                    .ConfigureAwait(false);
                await RefreshPendingCountAsync(outbox, cancellationToken).ConfigureAwait(false);
                return HeadOfficeSyncResult.Offline("Network unavailable — deltas queued locally for later upload.");
            }

            if (!TryCreateBaseUri(out var baseUri))
            {
                return HeadOfficeSyncResult.Failed("HeadOfficeSync:BaseUrl is not a valid absolute URI.");
            }

            var packaged = await PackageLocalDeltasAsync(outbox, shifts, inventory, config, connections, cancellationToken)
                .ConfigureAwait(false);
            var client = CreateClient();
            _isHeadOfficeReachable = await ProbeAsync(baseUri, cancellationToken).ConfigureAwait(false);
            if (!_isHeadOfficeReachable)
            {
                await RefreshPendingCountAsync(outbox, cancellationToken).ConfigureAwait(false);
                _connectionStatusText = "Offline — head office unreachable";
                RaiseStatusChanged();
                return HeadOfficeSyncResult.Offline("Head office unreachable — deltas remain queued.");
            }

            var uploaded = await UploadPendingAsync(outbox, terminals, client, baseUri, cancellationToken)
                .ConfigureAwait(false);

            var openShift = await shifts.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
            var sinceCatalog = await ReadTimestampAsync(config, LastCatalogConfigKey, cancellationToken)
                .ConfigureAwait(false);
            var catalogUri = Combine(baseUri, _options.CatalogDeltaPath);
            var catalogResult = await catalog.PullAndApplyCatalogAsync(
                    client,
                    catalogUri,
                    sinceCatalog,
                    activeSalesShiftOpen: openShift is not null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!catalogResult.Success)
            {
                _lastError = catalogResult.Error;
                _logger.LogWarning("Catalog replication failed: {Error}", catalogResult.Error);
            }
            else if (catalogResult.CatalogRevisionUtc is not null)
            {
                await config.UpsertJsonAsync(
                        LastCatalogConfigKey,
                        catalogResult.CatalogRevisionUtc.Value.ToUniversalTime().ToString("O"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var completed = DateTime.UtcNow;
            await config.UpsertJsonAsync(LastSyncConfigKey, completed.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
            _lastSyncTimestamp = completed;
            _lastError = catalogResult.Success ? null : catalogResult.Error;
            await RefreshPendingCountAsync(outbox, cancellationToken).ConfigureAwait(false);
            _connectionStatusText = BuildStatusText(true, failedCount: 0);

            var terminalId = await ResolveTerminalIdAsync(terminals, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(terminalId))
            {
                await terminals.UpdateLastSyncedAsync(terminalId, completed, cancellationToken).ConfigureAwait(false);
            }

            RaiseStatusChanged();

            return new HeadOfficeSyncResult
            {
                Enabled = true,
                Success = catalogResult.Success || uploaded > 0 || packaged > 0,
                PackagedCount = packaged,
                UploadedCount = uploaded,
                CatalogProductsApplied = catalogResult.ProductsApplied,
                ConflictsPreservedLocalStock = catalogResult.LocalStockPreserved,
                CompletedAtUtc = completed,
                Message = catalogResult.Success
                    ? $"Uploaded {uploaded} package(s); catalog applied {catalogResult.ProductsApplied}."
                    : $"Uploaded {uploaded}; catalog warning: {catalogResult.Error}",
                Error = catalogResult.Success ? null : catalogResult.Error
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Head-office sync cycle failed.");
            RaiseStatusChanged();
            return HeadOfficeSyncResult.Failed(ex.Message);
        }
        finally
        {
            _isSyncing = false;
            RaiseStatusChanged();
            _gate.Release();
        }
    }

    private async Task<int> PackageLocalDeltasAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        ICashierShiftRepository shifts,
        ILocalInventoryRepository inventory,
        IConfigurationRepository config,
        ISqlConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        var packaged = 0;
        packaged += await PackageClosedZReportsAsync(outbox, shifts, cancellationToken).ConfigureAwait(false);
        packaged += await PackageFinancialClosuresAsync(outbox, connections, cancellationToken).ConfigureAwait(false);
        packaged += await PackageSalesSummaryAsync(outbox, config, connections, cancellationToken).ConfigureAwait(false);
        packaged += await PackageInventorySnapshotAsync(outbox, inventory, cancellationToken).ConfigureAwait(false);
        return packaged;
    }

    private static async Task<int> PackageFinancialClosuresAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        ISqlConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (40)
                ClosureId, BusinessDate, ClosedAtUtc, ClosedByUsername, ClosedByDisplayName,
                TotalGrossSalesMwk, TotalVatCollectedMwk, CashDrawerVarianceMwk, AuditPassed, ClosureJson
            FROM dbo.FinancialClosures
            WHERE Status = N'Closed'
            ORDER BY ClosureId DESC;
            """;

        await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<FinancialClosureOutboxRow>(
                new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        var count = 0;
        foreach (var row in rows)
        {
            var correlation = $"FinancialClosure:{row.ClosureId}";
            if (await outbox.ExistsPendingOrUploadedAsync(
                    HeadOfficeSyncPayloadTypes.FinancialClosure, correlation, cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            object? closurePayload = null;
            if (!string.IsNullOrWhiteSpace(row.ClosureJson))
            {
                try
                {
                    closurePayload = JsonSerializer.Deserialize<JsonElement>(row.ClosureJson);
                }
                catch (JsonException)
                {
                    closurePayload = row.ClosureJson;
                }
            }

            var payload = new
            {
                closureId = row.ClosureId,
                businessDate = row.BusinessDate,
                closedAtUtc = row.ClosedAtUtc,
                closedByUsername = row.ClosedByUsername,
                closedByDisplayName = row.ClosedByDisplayName,
                totalGrossSalesMwk = row.TotalGrossSalesMwk,
                totalVatCollectedMwk = row.TotalVatCollectedMwk,
                cashDrawerVarianceMwk = row.CashDrawerVarianceMwk,
                auditPassed = row.AuditPassed,
                closure = closurePayload
            };

            await outbox.EnqueueAsync(
                    HeadOfficeSyncPayloadTypes.FinancialClosure,
                    correlation,
                    HeadOfficePayloadCipher.SerializePlainJson(payload),
                    cancellationToken)
                .ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static async Task<int> PackageClosedZReportsAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        ICashierShiftRepository shifts,
        CancellationToken cancellationToken)
    {
        var recent = await shifts.GetRecentShiftsAsync(40, cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var shift in recent.Where(s => s.Status == ShiftStatuses.Closed && !string.IsNullOrWhiteSpace(s.ZReportJson)))
        {
            var correlation = $"ZReport:{shift.ShiftId}";
            if (await outbox.ExistsPendingOrUploadedAsync(
                    HeadOfficeSyncPayloadTypes.ZReport, correlation, cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            var payload = new
            {
                shiftId = shift.ShiftId,
                cashierName = shift.CashierName,
                openedAtUtc = shift.OpenedAtUtc,
                closedAtUtc = shift.ClosedAtUtc,
                openingFloat = shift.OpeningFloat,
                closingCashCounted = shift.ClosingCashCounted,
                expectedCash = shift.ExpectedCash,
                cashVariance = shift.CashVariance,
                zReport = JsonSerializer.Deserialize<JsonElement>(shift.ZReportJson!)
            };

            await outbox.EnqueueAsync(
                    HeadOfficeSyncPayloadTypes.ZReport,
                    correlation,
                    HeadOfficePayloadCipher.SerializePlainJson(payload),
                    cancellationToken)
                .ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private async Task<int> PackageSalesSummaryAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        IConfigurationRepository config,
        ISqlConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        var since = await ReadTimestampAsync(config, LastSalesCursorConfigKey, cancellationToken).ConfigureAwait(false)
            ?? DateTime.UtcNow.Date.AddDays(-1);
        var toExclusive = DateTime.UtcNow;

        const string sql = """
            SELECT
                COUNT(*) AS InvoiceCount,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18,2)), 0)) AS GrossSales,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.totalVat') AS DECIMAL(18,2)), 0)) AS TotalVat
            FROM dbo.OfflineInvoiceQueue
            WHERE Status = N'SYNCED'
              AND CreatedAt >= @SinceUtc
              AND CreatedAt < @ToUtc;
            """;

        await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<SalesSummaryRow>(
            new CommandDefinition(sql, new { SinceUtc = since, ToUtc = toExclusive }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var correlation = $"SalesSummary:{since:yyyyMMddHH}:{toExclusive:yyyyMMddHH}";
        if (await outbox.ExistsPendingOrUploadedAsync(
                HeadOfficeSyncPayloadTypes.SalesSummary, correlation, cancellationToken)
            .ConfigureAwait(false))
        {
            return 0;
        }

        if (row.InvoiceCount == 0)
        {
            await config.UpsertJsonAsync(LastSalesCursorConfigKey, toExclusive.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        var payload = new
        {
            fromUtc = since,
            toUtcExclusive = toExclusive,
            invoiceCount = row.InvoiceCount,
            grossSales = row.GrossSales,
            totalVat = row.TotalVat
        };

        await outbox.EnqueueAsync(
                HeadOfficeSyncPayloadTypes.SalesSummary,
                correlation,
                HeadOfficePayloadCipher.SerializePlainJson(payload),
                cancellationToken)
            .ConfigureAwait(false);

        await config.UpsertJsonAsync(LastSalesCursorConfigKey, toExclusive.ToString("O"), cancellationToken)
            .ConfigureAwait(false);
        return 1;
    }

    private static async Task<int> PackageInventorySnapshotAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        ILocalInventoryRepository inventory,
        CancellationToken cancellationToken)
    {
        var items = await inventory.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var correlation = $"InventoryAdjustment:{DateTime.UtcNow:yyyyMMdd}";
        if (await outbox.ExistsPendingOrUploadedAsync(
                HeadOfficeSyncPayloadTypes.InventoryAdjustment, correlation, cancellationToken)
            .ConfigureAwait(false))
        {
            return 0;
        }

        var payload = new
        {
            capturedAtUtc = DateTime.UtcNow,
            itemCount = items.Count,
            adjustments = items.Select(i => new
            {
                i.ProductId,
                i.ProductCode,
                i.Name,
                i.StockQuantity,
                i.UnitPrice,
                i.HsCode,
                i.UnitOfMeasure,
                i.TaxRateId,
                i.CatalogSource
            }).ToList()
        };

        await outbox.EnqueueAsync(
                HeadOfficeSyncPayloadTypes.InventoryAdjustment,
                correlation,
                HeadOfficePayloadCipher.SerializePlainJson(payload),
                cancellationToken)
            .ConfigureAwait(false);
        return 1;
    }

    private async Task<int> UploadPendingAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        ITerminalRepository terminals,
        HttpClient client,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        var pending = await outbox.GetPendingAsync(_options.MaxBatchSize, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        byte[]? key = null;
        if (!string.IsNullOrWhiteSpace(_options.PayloadEncryptionKeyBase64))
        {
            key = HeadOfficePayloadCipher.ResolveKey(_options.PayloadEncryptionKeyBase64);
        }

        var branchId = ResolveBranchId();
        var terminalId = await ResolveTerminalIdAsync(terminals, cancellationToken).ConfigureAwait(false) ?? "UNKNOWN";
        var ids = pending.Select(p => p.OutboxId).ToList();
        await outbox.MarkUploadingAsync(ids, cancellationToken).ConfigureAwait(false);

        try
        {
            var packages = new List<HeadOfficeBranchSyncPackage>(pending.Count);
            foreach (var item in pending)
            {
                EncryptedHeadOfficeEnvelope envelope;
                if (key is null)
                {
                    envelope = new EncryptedHeadOfficeEnvelope
                    {
                        Algorithm = "NONE",
                        NonceBase64 = string.Empty,
                        TagBase64 = string.Empty,
                        CiphertextBase64 = Convert.ToBase64String(
                            System.Text.Encoding.UTF8.GetBytes(item.PlainJson))
                    };
                }
                else
                {
                    envelope = HeadOfficePayloadCipher.EncryptJson(item.PlainJson, key);
                }

                packages.Add(new HeadOfficeBranchSyncPackage
                {
                    BranchId = branchId,
                    TerminalId = terminalId,
                    PackagedAtUtc = DateTime.UtcNow,
                    PayloadType = item.PayloadType,
                    CorrelationKey = item.CorrelationKey,
                    EncryptedPayload = envelope
                });
            }

            var batch = new HeadOfficeUploadBatchRequest
            {
                BranchId = branchId,
                TerminalId = terminalId,
                SentAtUtc = DateTime.UtcNow,
                Packages = packages
            };

            var uploadUri = Combine(baseUri, _options.UploadPath);
            using var response = await client.PostAsJsonAsync(uploadUri, batch, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                await outbox.MarkFailedAsync(ids, $"HTTP {(int)response.StatusCode}: {Truncate(body, 400)}", cancellationToken)
                    .ConfigureAwait(false);
                return 0;
            }

            var ack = await response.Content.ReadFromJsonAsync<HeadOfficeUploadBatchResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (ack?.AcceptedCorrelationKeys is { Count: > 0 })
            {
                var acceptedIds = pending
                    .Where(p => ack.AcceptedCorrelationKeys.Contains(p.CorrelationKey, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p.OutboxId)
                    .ToList();
                var rejectedIds = ids.Except(acceptedIds).ToList();
                await outbox.MarkUploadedAsync(acceptedIds, cancellationToken).ConfigureAwait(false);
                if (rejectedIds.Count > 0)
                {
                    await outbox.MarkFailedAsync(
                            rejectedIds,
                            ack.Message ?? "Head office did not acknowledge package.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return acceptedIds.Count;
            }

            await outbox.MarkUploadedAsync(ids, cancellationToken).ConfigureAwait(false);
            return ids.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await outbox.MarkFailedAsync(ids, ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(HeadOfficeSyncService));
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.HttpTimeoutSeconds, 10, 180));
        if (!string.IsNullOrWhiteSpace(_options.AuthorizationHeader))
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _options.AuthorizationHeader);
        }

        return client;
    }

    private async Task<bool> ProbeAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, baseUri);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private bool TryCreateBaseUri(out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return false;
        }

        return Uri.TryCreate(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out baseUri!);
    }

    private static Uri Combine(Uri baseUri, string relativePath) =>
        new(baseUri, relativePath.TrimStart('/'));

    private string ResolveBranchId() =>
        !string.IsNullOrWhiteSpace(_options.BranchId) ? _options.BranchId : _deployment.BranchId;

    private async Task<string?> ResolveTerminalIdAsync(ITerminalRepository terminals, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.TerminalId))
        {
            return _options.TerminalId;
        }

        return await terminals.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DateTime?> ReadTimestampAsync(
        IConfigurationRepository config,
        string key,
        CancellationToken cancellationToken)
    {
        var json = await config.GetJsonAsync(key, cancellationToken).ConfigureAwait(false);
        return DateTime.TryParse(json, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime()
            : null;
    }

    private async Task RefreshPendingCountAsync(
        IHeadOfficeSyncOutboxRepository outbox,
        CancellationToken cancellationToken)
    {
        var counts = await outbox.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        _pendingUploadCount = counts.Pending + counts.Failed;
    }

    private string BuildStatusText(bool network, int failedCount)
    {
        if (!_options.Enabled)
        {
            return "Head office sync disabled";
        }

        if (!network)
        {
            return $"Offline — {_pendingUploadCount} pending upload(s)";
        }

        if (!_isHeadOfficeReachable)
        {
            return $"Online — head office unreachable ({_pendingUploadCount} pending)";
        }

        if (failedCount > 0)
        {
            return $"Online — {failedCount} failed upload(s), {_pendingUploadCount} pending";
        }

        return _lastSyncTimestamp is null
            ? $"Online — head office reachable ({_pendingUploadCount} pending)"
            : $"Synced {_lastSyncTimestamp.Value.ToLocalTime():g} ({_pendingUploadCount} pending)";
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private sealed class SalesSummaryRow
    {
        public int InvoiceCount { get; set; }
        public decimal GrossSales { get; set; }
        public decimal TotalVat { get; set; }
    }

    private sealed class FinancialClosureOutboxRow
    {
        public long ClosureId { get; set; }
        public DateTime BusinessDate { get; set; }
        public DateTime ClosedAtUtc { get; set; }
        public string ClosedByUsername { get; set; } = string.Empty;
        public string ClosedByDisplayName { get; set; } = string.Empty;
        public decimal TotalGrossSalesMwk { get; set; }
        public decimal TotalVatCollectedMwk { get; set; }
        public decimal CashDrawerVarianceMwk { get; set; }
        public bool AuditPassed { get; set; }
        public string? ClosureJson { get; set; }
    }
}

public sealed class HeadOfficeSyncBackgroundService : BackgroundService
{
    private readonly IHeadOfficeSyncService _syncService;
    private readonly IOptions<HeadOfficeSyncOptions> _options;
    private readonly ILogger<HeadOfficeSyncBackgroundService> _logger;

    public HeadOfficeSyncBackgroundService(
        IHeadOfficeSyncService syncService,
        IOptions<HeadOfficeSyncOptions> options,
        ILogger<HeadOfficeSyncBackgroundService> logger)
    {
        _syncService = syncService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.Value.PollIntervalSeconds, 30, 3600));
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Value.Enabled)
                {
                    await _syncService.SyncNowAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Background head-office sync cycle failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
