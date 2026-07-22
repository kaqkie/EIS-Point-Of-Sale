using System.IO;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase37ButtonContrastAndSqlKeywordTests
{
    [Fact]
    public void DatabaseBootstrapOptions_DefaultsToPhase37SchemaVersion()
    {
        var options = new DatabaseBootstrapOptions();
        Assert.Equal(32, options.TargetSchemaVersion);
    }

    [Fact]
    public void FirstRunBootstrap_InitialSetupRelativePath_IsCanonical()
    {
        Assert.Equal(@"Database\Scripts\InitialSetup.sql", FirstRunBootstrapService.InitialSetupScriptRelativePath);
    }

    [Fact]
    public void SplitSqlGoBatches_SplitsOnStandaloneGoAndKeepsDelimitedTrigger()
    {
        const string script = """
            SET NOCOUNT ON;
            GO
            CREATE TABLE dbo.DatabaseBackupHistory
            (
                BackupId BIGINT NOT NULL,
                [Trigger] VARCHAR(40) NOT NULL
            );
            GO
            PRINT N'done';
            GO
            """;

        var batches = FirstRunBootstrapService.SplitSqlGoBatches(script);

        Assert.Equal(3, batches.Count);
        Assert.Contains("[Trigger]", batches[1], StringComparison.Ordinal);
        Assert.DoesNotContain("GO", batches[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitialSetupScript_WhenPresent_DelimitTriggerAndUsesGoBatches()
    {
        var path = FirstRunBootstrapService.ResolveInitialSetupScriptPath();
        if (path is null)
        {
            // Output copy may be absent in some test hosts — skip without failing CI layout.
            return;
        }

        var text = File.ReadAllText(path);
        Assert.Contains("[Trigger]", text, StringComparison.Ordinal);
        Assert.Contains("\nGO\n", text.Replace("\r\n", "\n"), StringComparison.Ordinal);

        var batches = FirstRunBootstrapService.SplitSqlGoBatches(text);
        Assert.True(batches.Count >= 3);
        Assert.Contains(batches, b => b.Contains("[Trigger]", StringComparison.Ordinal));
    }
}
