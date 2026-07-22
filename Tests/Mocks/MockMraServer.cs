using PointOfSale.Infrastructure.Testing;

namespace PointOfSale.Tests.Mocks;

/// <summary>
/// Test-project alias for the shared MRA EIS mock server (Phase 26 sandbox harness).
/// </summary>
public sealed class MockMraServer : MockMraEisServer
{
    public new IReadOnlyList<RecordedMraRequest> SalesRequests =>
        base.SalesRequests
            .Select(r => new RecordedMraRequest(r.Method, r.Path, r.Body, r.Headers))
            .ToList();

    public new IReadOnlyList<RecordedMraRequest> InitialInventoryRequests =>
        base.InitialInventoryRequests
            .Select(r => new RecordedMraRequest(r.Method, r.Path, r.Body, r.Headers))
            .ToList();
}

public sealed record RecordedMraRequest(
    string Method,
    string Path,
    string? Body,
    IReadOnlyDictionary<string, IEnumerable<string>> Headers);
