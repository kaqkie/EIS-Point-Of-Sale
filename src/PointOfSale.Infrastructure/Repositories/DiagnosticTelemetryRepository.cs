using System.Text.Json;
using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IDiagnosticTelemetryRepository
{
    Task InsertAsync(DiagnosticTelemetryEvent entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiagnosticTelemetryEvent>> GetRecentAsync(
        int take,
        string? categoryFilter = null,
        string? severityFilter = null,
        string? search = null,
        CancellationToken cancellationToken = default);
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

public sealed class DiagnosticTelemetryRepository : IDiagnosticTelemetryRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public DiagnosticTelemetryRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InsertAsync(DiagnosticTelemetryEvent entry, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.DiagnosticTelemetryEvents
                (Category, Severity, Source, Message, DetailJson, LatencyMs, HttpStatus)
            VALUES
                (@Category, @Severity, @Source, @Message, @DetailJson, @LatencyMs, @HttpStatus);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, entry, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticTelemetryEvent>> GetRecentAsync(
        int take,
        string? categoryFilter = null,
        string? severityFilter = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                EventId, CreatedAtUtc, Category, Severity, Source, Message, DetailJson, LatencyMs, HttpStatus
            FROM dbo.DiagnosticTelemetryEvents
            WHERE (@Category IS NULL OR Category = @Category)
              AND (@Severity IS NULL OR Severity = @Severity)
              AND (
                    @Search IS NULL
                    OR Message LIKE '%' + @Search + '%'
                    OR Source LIKE '%' + @Search + '%'
                    OR DetailJson LIKE '%' + @Search + '%'
                  )
            ORDER BY CreatedAtUtc DESC, EventId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<DiagnosticTelemetryEvent>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        Take = Math.Clamp(take, 1, 500),
                        Category = string.IsNullOrWhiteSpace(categoryFilter) || categoryFilter == "All"
                            ? null
                            : categoryFilter,
                        Severity = string.IsNullOrWhiteSpace(severityFilter) || severityFilter == "All"
                            ? null
                            : severityFilter,
                        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim()
                    },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM dbo.DiagnosticTelemetryEvents
            WHERE CreatedAtUtc < @CutoffUtc;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(sql, new { CutoffUtc = cutoffUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}

public static class DiagnosticDetailJson
{
    public static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value);
}
