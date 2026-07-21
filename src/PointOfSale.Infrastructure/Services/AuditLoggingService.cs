using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Security;

namespace PointOfSale.Infrastructure.Services;

public interface IAuditLoggingService
{
    Task LogMraExchangeAsync(
        string httpMethod,
        string requestPath,
        int? httpStatusCode,
        int durationMs,
        string? requestBody,
        string? responseBody,
        bool isSuccess,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
}

public sealed class AuditLoggingService : IAuditLoggingService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly AuditLoggingOptions _options;
    private readonly ILogger<AuditLoggingService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public AuditLoggingService(
        ISqlConnectionFactory connectionFactory,
        IOptions<AuditLoggingOptions> options,
        ILogger<AuditLoggingService> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task LogMraExchangeAsync(
        string httpMethod,
        string requestPath,
        int? httpStatusCode,
        int durationMs,
        string? requestBody,
        string? responseBody,
        bool isSuccess,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var scrubbedRequest = SensitiveDataScrubber.Scrub(requestBody);
        var scrubbedResponse = SensitiveDataScrubber.Scrub(responseBody);
        var scrubbedError = SensitiveDataScrubber.Scrub(errorMessage);

        _logger.LogInformation(
            "MRA {Method} {Path} status={Status} success={Success} durationMs={Duration}",
            httpMethod,
            requestPath,
            httpStatusCode,
            isSuccess,
            durationMs);

        if (_options.EnableFileAudit)
        {
            await WriteRollingFileAsync(
                httpMethod,
                requestPath,
                httpStatusCode,
                durationMs,
                scrubbedRequest,
                scrubbedResponse,
                isSuccess,
                scrubbedError,
                cancellationToken).ConfigureAwait(false);
        }

        if (!_options.EnableDatabaseAudit)
        {
            return;
        }

        const string sql = """
            INSERT INTO dbo.MraApiAuditLog
                (HttpMethod, RequestPath, HttpStatusCode, DurationMs, RequestBody, ResponseBody, IsSuccess, ErrorMessage)
            VALUES
                (@HttpMethod, @RequestPath, @HttpStatusCode, @DurationMs, @RequestBody, @ResponseBody, @IsSuccess, @ErrorMessage);
            """;

        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        HttpMethod = httpMethod,
                        RequestPath = Truncate(requestPath, 500),
                        HttpStatusCode = httpStatusCode,
                        DurationMs = durationMs,
                        RequestBody = Truncate(scrubbedRequest, 4000),
                        ResponseBody = Truncate(scrubbedResponse, 8000),
                        IsSuccess = isSuccess,
                        ErrorMessage = Truncate(scrubbedError, 2000)
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist MRA audit row for {Path}.", requestPath);
        }
    }

    private async Task WriteRollingFileAsync(
        string httpMethod,
        string requestPath,
        int? httpStatusCode,
        int durationMs,
        string requestBody,
        string responseBody,
        bool isSuccess,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, _options.FileDirectory);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"mra-audit-{DateTime.UtcNow:yyyyMMdd}.log");

        var builder = new StringBuilder();
        builder.AppendLine($"----- {DateTime.UtcNow:O} -----");
        builder.AppendLine($"METHOD: {httpMethod}");
        builder.AppendLine($"PATH: {requestPath}");
        builder.AppendLine($"STATUS: {httpStatusCode}");
        builder.AppendLine($"SUCCESS: {isSuccess}");
        builder.AppendLine($"DURATION_MS: {durationMs}");
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            builder.AppendLine($"ERROR: {errorMessage}");
        }

        builder.AppendLine("REQUEST:");
        builder.AppendLine(string.IsNullOrWhiteSpace(requestBody) ? "(empty)" : requestBody);
        builder.AppendLine("RESPONSE:");
        builder.AppendLine(string.IsNullOrWhiteSpace(responseBody) ? "(empty)" : responseBody);
        builder.AppendLine();

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(filePath, builder.ToString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }

        PurgeOldAuditFiles(directory);
    }

    private void PurgeOldAuditFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.RetainedFileDays);
            foreach (var file in Directory.EnumerateFiles(directory, "mra-audit-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audit file purge skipped.");
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
