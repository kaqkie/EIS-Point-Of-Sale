using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PointOfSale.App.Services.Compliance;

public sealed class MraCertificationAuditDocument
{
    public string PackageId { get; set; } = Guid.NewGuid().ToString("N");

    public string TerminalId { get; set; } = "UNASSIGNED";

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string OverallResult { get; set; } = "InProgress";

    public string ApplicationVersion { get; set; } = string.Empty;

    public IList<MraCertificationStepResult> Steps { get; set; } = new List<MraCertificationStepResult>();
}

public sealed class MraCertificationStepResult
{
    public required string Scenario { get; set; }

    public required string Endpoint { get; set; }

    public DateTime TimestampUtc { get; set; }

    public bool Passed { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? RequestPayload { get; set; }

    public string? ResponsePayload { get; set; }

    public string? ResponseSignatureOrFiscalCode { get; set; }

    public string? XSignatureHeader { get; set; }

    public string? Error { get; set; }

    public long DurationMs { get; set; }
}

public interface IMraCertificationAuditStore
{
    string AuditFilePath { get; }

    Task SaveAsync(MraCertificationAuditDocument document, CancellationToken cancellationToken = default);

    Task<MraCertificationAuditDocument?> LoadAsync(CancellationToken cancellationToken = default);

    void AppendStatus(string message);
}

public sealed class MraCertificationAuditStore : IMraCertificationAuditStore
{
    public const string RelativeAuditPath = "Logs/MraCertificationAudit.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly List<string> _statusLines = new();

    public string AuditFilePath =>
        Path.Combine(AppContext.BaseDirectory, RelativeAuditPath.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<string> StatusLines
    {
        get
        {
            lock (_gate)
            {
                return _statusLines.ToList();
            }
        }
    }

    public void AppendStatus(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_gate)
        {
            _statusLines.Add(line);
        }
    }

    public void ClearStatus()
    {
        lock (_gate)
        {
            _statusLines.Clear();
        }
    }

    public async Task SaveAsync(MraCertificationAuditDocument document, CancellationToken cancellationToken = default)
    {
        var path = AuditFilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        AppendStatus($"Audit written: {path}");
    }

    public async Task<MraCertificationAuditDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = AuditFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MraCertificationAuditDocument>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
