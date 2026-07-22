using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Testing;

namespace PointOfSale.App.Services;

public interface IIntegrationTestDashboardService
{
    Task<IntegrationSuiteReport> RunSandboxSuiteAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IntegrationTestProcessResult> RunDotNetTestSuiteAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> WriteComplianceReadinessReportAsync(
        IntegrationSuiteReport report,
        CancellationToken cancellationToken = default);
}

public sealed class IntegrationTestProcessResult
{
    public bool Success { get; init; }
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public string Output { get; init; } = string.Empty;
}

public sealed class IntegrationTestDashboardService : IIntegrationTestDashboardService
{
    private readonly IAuthenticationAuthorizationService _auth;

    public IntegrationTestDashboardService(IAuthenticationAuthorizationService auth)
    {
        _auth = auth;
    }

    public async Task<IntegrationSuiteReport> RunSandboxSuiteAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.RunIntegrationTests);

        return await Task.Run(async () =>
        {
            using var harness = new MraSandboxSimulationHarness();
            return await harness.RunStandardSuiteAsync(progress, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IntegrationTestProcessResult> RunDotNetTestSuiteAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.RunIntegrationTests);

        var solution = FindSolutionPath();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{solution}\" -c Release --filter \"FullyQualifiedName~PointOfSaleIntegrationTests|MraSandboxSimulationHarness|MraIntegrationTests\" --logger \"console;verbosity=normal\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet test.");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var combined = output + Environment.NewLine + error;
        progress?.Report(combined);

        var pass = ParseCount(combined, "Passed:");
        var fail = ParseCount(combined, "Failed:");

        return new IntegrationTestProcessResult
        {
            Success = process.ExitCode == 0,
            PassCount = pass,
            FailCount = fail,
            Output = combined
        };
    }

    public async Task<string> WriteComplianceReadinessReportAsync(
        IntegrationSuiteReport report,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.RunIntegrationTests);

        var directory = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "IntegrationComplianceReadiness.json");

        var envelope = new
        {
            generatedUtc = DateTime.UtcNow,
            report.AllPassed,
            report.PassCount,
            report.FailCount,
            report.FailureLog,
            scenarios = report.Scenarios
        };

        await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);

        return path;
    }

    private static int ParseCount(string output, string token)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return 0;
        }

        var digits = new string(line.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    private static string FindSolutionPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "PointOfSale.sln");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("PointOfSale.sln not found from application base directory.");
    }
}
