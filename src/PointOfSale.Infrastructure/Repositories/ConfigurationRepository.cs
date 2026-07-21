using System.Text.Json;
using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Security;

namespace PointOfSale.Infrastructure.Repositories;

public interface IConfigurationRepository
{
    Task UpsertJsonAsync(string configKey, string configJson, CancellationToken cancellationToken = default);
    Task<string?> GetJsonAsync(string configKey, CancellationToken cancellationToken = default);
    Task UpsertProtectedSecretAsync(string configKey, string plainSecret, CancellationToken cancellationToken = default);
    Task<string?> GetProtectedSecretPlainAsync(string configKey, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationRepository : IConfigurationRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ISecretProtector _secretProtector;

    public ConfigurationRepository(ISqlConnectionFactory connectionFactory, ISecretProtector secretProtector)
    {
        _connectionFactory = connectionFactory;
        _secretProtector = secretProtector;
    }

    public async Task UpsertJsonAsync(string configKey, string configJson, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.Configurations AS target
            USING (SELECT @ConfigKey AS ConfigKey) AS source
            ON target.ConfigKey = source.ConfigKey
            WHEN MATCHED THEN
                UPDATE SET ConfigJson = @ConfigJson, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (@ConfigKey, @ConfigJson, GETUTCDATE());
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { ConfigKey = configKey, ConfigJson = configJson }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<string?> GetJsonAsync(string configKey, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT ConfigJson FROM dbo.Configurations WHERE ConfigKey = @ConfigKey;";

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(sql, new { ConfigKey = configKey }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertProtectedSecretAsync(string configKey, string plainSecret, CancellationToken cancellationToken = default)
    {
        var envelope = JsonSerializer.Serialize(new ProtectedSecretEnvelope
        {
            ProtectedValue = _secretProtector.Protect(plainSecret)
        });

        await UpsertJsonAsync(configKey, envelope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetProtectedSecretPlainAsync(string configKey, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync(configKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ProtectedSecretEnvelope>(json);
        return envelope?.ProtectedValue is null
            ? null
            : _secretProtector.Unprotect(envelope.ProtectedValue);
    }

    private sealed class ProtectedSecretEnvelope
    {
        public string? ProtectedValue { get; set; }
    }
}
