using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class ApplicationUpdateTests
{
    [Fact]
    public void UpdateCheckResult_UpdateStaged_MarksAvailableAndStaged()
    {
        var result = UpdateCheckResult.UpdateStaged(
            new Version(1, 0, 0, 0),
            new Version(1, 0, 1, 0),
            "Bug fixes",
            mandatory: false);

        Assert.True(result.Enabled);
        Assert.True(result.UpdateAvailable);
        Assert.True(result.Staged);
        Assert.Equal(new Version(1, 0, 1, 0), result.AvailableVersion);
        Assert.Equal("Bug fixes", result.ReleaseNotes);
    }

    [Fact]
    public void DatabaseBootstrap_SchemaVersionKey_IsStable()
    {
        Assert.Equal("Schema.Version", DatabaseBootstrapService.SchemaVersionConfigKey);
    }
}
