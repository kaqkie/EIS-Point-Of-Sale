using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IMultiTerminalSyncBroker
{
    event EventHandler? StatusChanged;

    bool IsSyncing { get; }
    DateTime? LastSyncUtc { get; }
    string ConnectionStatusText { get; }
    string? LastError { get; }

    Task<MultiTerminalSyncStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<MultiTerminalSyncResult> SynchronizeNowAsync(CancellationToken cancellationToken = default);

    Task PublishInventoryDeltaAsync(
        string productCode,
        decimal quantityDelta,
        CancellationToken cancellationToken = default);

    Task PublishShiftTotalsAsync(
        string? cashierName,
        decimal expectedCash,
        CancellationToken cancellationToken = default);
}

public sealed class MultiTerminalSyncStatusSnapshot
{
    public bool Enabled { get; init; }
    public bool IsSyncing { get; init; }
    public string TerminalId { get; init; } = string.Empty;
    public string BranchId { get; init; } = string.Empty;
    public DateTime? LastSyncUtc { get; init; }
    public string ConnectionStatusText { get; init; } = string.Empty;
    public string? LastError { get; init; }
    public int OnlineTerminalCount { get; init; }
    public int OfflineTerminalCount { get; init; }
    public int PendingLedgerCount { get; init; }
    public int PendingOfflineInvoices { get; init; }
    public IReadOnlyList<TerminalHeartbeatRow> Peers { get; init; } = Array.Empty<TerminalHeartbeatRow>();
}

public sealed class MultiTerminalSyncResult
{
    public bool Enabled { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public int AppliedLedgerCount { get; init; }
    public int InventoryDeltasApplied { get; init; }

    public static MultiTerminalSyncResult Disabled(string message) => new()
    {
        Enabled = false,
        Success = true,
        Message = message
    };
}

internal sealed class InventoryDeltaPayload
{
    public string ProductCode { get; set; } = string.Empty;
    public decimal QuantityDelta { get; set; }
}

internal sealed class ShiftTotalsPayload
{
    public string? CashierName { get; set; }
    public decimal ExpectedCash { get; set; }
}

internal sealed class OfflineQueueSnapshotPayload
{
    public int PendingCount { get; set; }
}

/// <summary>
/// Local backroom sync broker coordinating multi-register consistency for inventory,
/// shift totals, and offline invoice queue visibility on a shared SQL Express store database.
/// </summary>
public sealed class MultiTerminalSyncBroker : IMultiTerminalSyncBroker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly MultiTerminalSyncOptions _options;
    private readonly HeadOfficeSyncOptions _headOffice;
    private readonly TerminalDeploymentOptions _deployment;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MultiTerminalSyncBroker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _isSyncing;
    private DateTime? _lastSyncUtc;
    private string _connectionStatusText = "Multi-terminal sync idle";
    private string? _lastError;

    public MultiTerminalSyncBroker(
        IOptions<MultiTerminalSyncOptions> options,
        IOptions<HeadOfficeSyncOptions> headOffice,
        IOptions<TerminalDeploymentOptions> deployment,
        IServiceScopeFactory scopeFactory,
        ILogger<MultiTerminalSyncBroker> logger)
    {
        _options = options.Value;
        _headOffice = headOffice.Value;
        _deployment = deployment.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public event EventHandler? StatusChanged;

    public bool IsSyncing => _isSyncing;
    public DateTime? LastSyncUtc => _lastSyncUtc;
    public string ConnectionStatusText => _connectionStatusText;
    public string? LastError => _lastError;

    public async Task<MultiTerminalSyncStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMultiTerminalSyncRepository>();
        var shifts = scope.ServiceProvider.GetRequiredService<ICashierShiftRepository>();

        var branchId = ResolveBranchId();
        var terminalId = ResolveTerminalId();
        var pendingInvoices = await repo.CountPendingOfflineInvoicesAsync(cancellationToken).ConfigureAwait(false);
        var openShift = await shifts.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);

        await repo.UpsertHeartbeatAsync(
                new TerminalHeartbeatRow
                {
                    TerminalId = terminalId,
                    BranchId = branchId,
                    LastSeenUtc = DateTime.UtcNow,
                    Status = "Online",
                    HostName = Environment.MachineName,
                    PendingOfflineInvoices = pendingInvoices,
                    OpenShiftExpectedCash = openShift?.ExpectedCash ?? 0m,
                    OpenShiftCashier = openShift?.CashierName
                },
                cancellationToken)
            .ConfigureAwait(false);

        var staleBefore = DateTime.UtcNow.AddSeconds(-Math.Max(15, _options.HeartbeatStaleSeconds));
        await repo.MarkStaleOfflineAsync(branchId, staleBefore, cancellationToken).ConfigureAwait(false);

        var peers = await repo.GetHeartbeatsAsync(branchId, cancellationToken).ConfigureAwait(false);
        var lastSeq = await repo.GetLastAppliedSequenceAsync(branchId, terminalId, cancellationToken)
            .ConfigureAwait(false) ?? 0L;
        var pending = await repo.GetPendingLedgerAsync(
                branchId,
                terminalId,
                lastSeq,
                Math.Max(1, _options.MaxBatchSize),
                cancellationToken)
            .ConfigureAwait(false);

        var online = peers.Count(p => string.Equals(p.Status, "Online", StringComparison.OrdinalIgnoreCase));
        var offline = peers.Count - online;
        _connectionStatusText = _options.Enabled
            ? $"{online} online / {offline} offline · pending ledger {pending.Count}"
            : "Multi-terminal sync disabled";

        RaiseStatusChanged();

        return new MultiTerminalSyncStatusSnapshot
        {
            Enabled = _options.Enabled,
            IsSyncing = _isSyncing,
            TerminalId = terminalId,
            BranchId = branchId,
            LastSyncUtc = _lastSyncUtc,
            ConnectionStatusText = _connectionStatusText,
            LastError = _lastError,
            OnlineTerminalCount = online,
            OfflineTerminalCount = offline,
            PendingLedgerCount = pending.Count,
            PendingOfflineInvoices = pendingInvoices,
            Peers = peers
        };
    }

    public async Task<MultiTerminalSyncResult> SynchronizeNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return MultiTerminalSyncResult.Disabled("Multi-terminal sync is disabled in configuration.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new MultiTerminalSyncResult
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
            var repo = scope.ServiceProvider.GetRequiredService<IMultiTerminalSyncRepository>();
            var shifts = scope.ServiceProvider.GetRequiredService<ICashierShiftRepository>();

            var branchId = ResolveBranchId();
            var terminalId = ResolveTerminalId();
            var pendingInvoices = await repo.CountPendingOfflineInvoicesAsync(cancellationToken).ConfigureAwait(false);
            var openShift = await shifts.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);

            await repo.UpsertHeartbeatAsync(
                    new TerminalHeartbeatRow
                    {
                        TerminalId = terminalId,
                        BranchId = branchId,
                        LastSeenUtc = DateTime.UtcNow,
                        Status = "Online",
                        HostName = Environment.MachineName,
                        PendingOfflineInvoices = pendingInvoices,
                        OpenShiftExpectedCash = openShift?.ExpectedCash ?? 0m,
                        OpenShiftCashier = openShift?.CashierName
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await repo.EnqueueLedgerAsync(
                    new MultiTerminalSyncLedgerItem
                    {
                        BranchId = branchId,
                        SourceTerminalId = terminalId,
                        EventType = MultiTerminalSyncEventTypes.OfflineQueueSnapshot,
                        EntityKey = "OfflineInvoiceQueue",
                        PayloadJson = JsonSerializer.Serialize(
                            new OfflineQueueSnapshotPayload { PendingCount = pendingInvoices },
                            JsonOptions)
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (openShift is not null)
            {
                await PublishShiftTotalsCoreAsync(
                        repo,
                        branchId,
                        terminalId,
                        openShift.CashierName,
                        openShift.ExpectedCash ?? 0m,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var lastSeq = await repo.GetLastAppliedSequenceAsync(branchId, terminalId, cancellationToken)
                .ConfigureAwait(false) ?? 0L;
            var pending = await repo.GetPendingLedgerAsync(
                    branchId,
                    terminalId,
                    lastSeq,
                    Math.Max(1, _options.MaxBatchSize),
                    cancellationToken)
                .ConfigureAwait(false);

            var applied = 0;
            var inventoryApplied = 0;
            long maxSequence = 0;

            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                maxSequence = Math.Max(maxSequence, item.SequenceNumber);

                // Shared SQL Express store DB: inventory is already mutated under lock by the source
                // terminal. Peer registers only advance the ledger cursor to avoid double-counting.
                if (string.Equals(item.EventType, MultiTerminalSyncEventTypes.InventoryDelta, StringComparison.Ordinal))
                {
                    inventoryApplied++;
                }

                await repo.MarkLedgerAppliedAsync(item.LedgerId, terminalId, cancellationToken)
                    .ConfigureAwait(false);
                applied++;
            }

            if (maxSequence > 0)
            {
                await repo.SetLastAppliedSequenceAsync(branchId, terminalId, maxSequence, cancellationToken)
                    .ConfigureAwait(false);
            }

            var staleBefore = DateTime.UtcNow.AddSeconds(-Math.Max(15, _options.HeartbeatStaleSeconds));
            await repo.MarkStaleOfflineAsync(branchId, staleBefore, cancellationToken).ConfigureAwait(false);

            _lastSyncUtc = DateTime.UtcNow;
            _lastError = null;
            _connectionStatusText = $"Synced {applied} ledger row(s); inventory deltas {inventoryApplied}.";
            RaiseStatusChanged();

            return new MultiTerminalSyncResult
            {
                Enabled = true,
                Success = true,
                Message = _connectionStatusText,
                AppliedLedgerCount = applied,
                InventoryDeltasApplied = inventoryApplied
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            _connectionStatusText = "Multi-terminal sync failed";
            _logger.LogError(ex, "Multi-terminal synchronize failed.");
            RaiseStatusChanged();
            return new MultiTerminalSyncResult
            {
                Enabled = true,
                Success = false,
                Error = ex.Message
            };
        }
        finally
        {
            _isSyncing = false;
            _gate.Release();
            RaiseStatusChanged();
        }
    }

    public async Task PublishInventoryDeltaAsync(
        string productCode,
        decimal quantityDelta,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(productCode) || quantityDelta == 0m)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMultiTerminalSyncRepository>();
        var branchId = ResolveBranchId();
        var terminalId = ResolveTerminalId();

        // Apply locally first under lock, then publish for peer awareness / catch-up.
        var applied = await repo.ApplyInventoryDeltaWithLockAsync(
                productCode.Trim(),
                quantityDelta,
                _options.InventoryLockTimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);

        if (!applied)
        {
            throw new InvalidOperationException(
                $"Could not acquire inventory lock for '{productCode}' within {_options.InventoryLockTimeoutMs}ms.");
        }

        await repo.EnqueueLedgerAsync(
                new MultiTerminalSyncLedgerItem
                {
                    BranchId = branchId,
                    SourceTerminalId = terminalId,
                    EventType = MultiTerminalSyncEventTypes.InventoryDelta,
                    EntityKey = productCode.Trim(),
                    PayloadJson = JsonSerializer.Serialize(
                        new InventoryDeltaPayload
                        {
                            ProductCode = productCode.Trim(),
                            QuantityDelta = quantityDelta
                        },
                        JsonOptions)
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishShiftTotalsAsync(
        string? cashierName,
        decimal expectedCash,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMultiTerminalSyncRepository>();
        await PublishShiftTotalsCoreAsync(
                repo,
                ResolveBranchId(),
                ResolveTerminalId(),
                cashierName,
                expectedCash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PublishShiftTotalsCoreAsync(
        IMultiTerminalSyncRepository repo,
        string branchId,
        string terminalId,
        string? cashierName,
        decimal expectedCash,
        CancellationToken cancellationToken)
    {
        await repo.EnqueueLedgerAsync(
                new MultiTerminalSyncLedgerItem
                {
                    BranchId = branchId,
                    SourceTerminalId = terminalId,
                    EventType = MultiTerminalSyncEventTypes.ShiftTotals,
                    EntityKey = "OpenShift",
                    PayloadJson = JsonSerializer.Serialize(
                        new ShiftTotalsPayload
                        {
                            CashierName = cashierName,
                            ExpectedCash = expectedCash
                        },
                        JsonOptions)
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private string ResolveTerminalId()
    {
        if (!string.IsNullOrWhiteSpace(_options.TerminalId))
        {
            return _options.TerminalId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_headOffice.TerminalId))
        {
            return _headOffice.TerminalId.Trim();
        }

        return Environment.MachineName;
    }

    private string ResolveBranchId()
    {
        if (!string.IsNullOrWhiteSpace(_options.BranchId))
        {
            return _options.BranchId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_deployment.BranchId))
        {
            return _deployment.BranchId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_headOffice.BranchId))
        {
            return _headOffice.BranchId.Trim();
        }

        return "LOCAL";
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MultiTerminalSyncBackgroundService : BackgroundService
{
    private readonly IMultiTerminalSyncBroker _broker;
    private readonly IOptions<MultiTerminalSyncOptions> _options;
    private readonly ILogger<MultiTerminalSyncBackgroundService> _logger;

    public MultiTerminalSyncBackgroundService(
        IMultiTerminalSyncBroker broker,
        IOptions<MultiTerminalSyncOptions> options,
        ILogger<MultiTerminalSyncBackgroundService> logger)
    {
        _broker = broker;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.Value.PollIntervalSeconds, 5, 300));
        await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Value.Enabled)
                {
                    await _broker.SynchronizeNowAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Background multi-terminal sync cycle failed.");
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
