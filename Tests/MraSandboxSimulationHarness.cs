using PointOfSale.Infrastructure.Testing;

namespace PointOfSale.Tests;

/// <summary>
/// Test-assembly entry type for the MRA EIS sandbox simulation harness (Phase 26).
/// </summary>
public sealed class MraSandboxSimulationHarness : Infrastructure.Testing.MraSandboxSimulationHarness
{
    public MraSandboxSimulationHarness(TimeSpan? httpTimeout = null)
        : base(httpTimeout)
    {
    }
}
