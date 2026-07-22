using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Testing;

namespace PointOfSale.App.ViewModels;

public partial class TestRunnerDashboardViewModel : ObservableObject
{
    private readonly IIntegrationTestDashboardService _runner;
    private readonly IAuthenticationAuthorizationService _auth;

    public TestRunnerDashboardViewModel(
        IIntegrationTestDashboardService runner,
        IAuthenticationAuthorizationService auth)
    {
        _runner = runner;
        _auth = auth;
        TestSuitesList = new ObservableCollection<IntegrationTestSuiteRowViewModel>();
    }

    public ObservableCollection<IntegrationTestSuiteRowViewModel> TestSuitesList { get; }

    [ObservableProperty]
    private string _executionStatus = "Ready to execute sandbox integration scenarios.";

    [ObservableProperty]
    private int _passCount;

    [ObservableProperty]
    private int _failCount;

    [ObservableProperty]
    private string _failureLogOutput = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressPercent;

    [RelayCommand]
    private async Task RunSandboxSuiteAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            IsRunning = true;
            ProgressPercent = 0;
            _auth.EnsurePermission(OperatorPermissions.RunIntegrationTests);
            TestSuitesList.Clear();
            ExecutionStatus = "Running MRA sandbox harness...";

            var progress = new Progress<string>(msg =>
            {
                ExecutionStatus = msg;
            });

            var report = await _runner.RunSandboxSuiteAsync(progress).ConfigureAwait(true);
            ApplyReport(report);
            var path = await _runner.WriteComplianceReadinessReportAsync(report).ConfigureAwait(true);
            ExecutionStatus = report.AllPassed
                ? $"All sandbox scenarios passed. Report: {path}"
                : $"Failures detected — see log. Report: {path}";
        }
        catch (Exception ex)
        {
            ExecutionStatus = ex.Message;
        }
        finally
        {
            IsRunning = false;
            ProgressPercent = 100;
        }
    }

    [RelayCommand]
    private async Task RunFullDotNetSuiteAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            IsRunning = true;
            ProgressPercent = 10;
            _auth.EnsurePermission(OperatorPermissions.RunIntegrationTests);
            ExecutionStatus = "Starting dotnet test (integration filter)...";

            var progress = new Progress<string>(msg => ExecutionStatus = "dotnet test running...");
            var result = await _runner.RunDotNetTestSuiteAsync(progress).ConfigureAwait(true);

            PassCount = result.PassCount;
            FailCount = result.FailCount;
            FailureLogOutput = result.Output;
            ProgressPercent = 100;
            ExecutionStatus = result.Success
                ? $"dotnet test completed — {result.PassCount} passed."
                : $"dotnet test failed — {result.FailCount} failed.";
        }
        catch (Exception ex)
        {
            ExecutionStatus = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task GenerateComplianceReportAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            IsRunning = true;
            _auth.EnsurePermission(OperatorPermissions.RunIntegrationTests);
            using var harness = new MraSandboxSimulationHarness();
            var report = await harness.RunStandardSuiteAsync().ConfigureAwait(true);
            ApplyReport(report);
            var path = await _runner.WriteComplianceReadinessReportAsync(report).ConfigureAwait(true);
            ExecutionStatus = $"Compliance readiness report written to {path}";
        }
        catch (Exception ex)
        {
            ExecutionStatus = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void ApplyReport(IntegrationSuiteReport report)
    {
        TestSuitesList.Clear();
        foreach (var scenario in report.Scenarios)
        {
            TestSuitesList.Add(new IntegrationTestSuiteRowViewModel
            {
                Name = scenario.Name,
                Passed = scenario.Passed,
                DurationMs = scenario.DurationMs,
                Message = scenario.Message
            });
        }

        PassCount = report.PassCount;
        FailCount = report.FailCount;
        FailureLogOutput = string.IsNullOrWhiteSpace(report.FailureLog)
            ? "No failures."
            : report.FailureLog;
        ProgressPercent = report.Scenarios.Count == 0
            ? 0
            : (double)report.PassCount / report.Scenarios.Count * 100d;
    }
}

public sealed class IntegrationTestSuiteRowViewModel
{
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public long DurationMs { get; init; }
    public string Message { get; init; } = string.Empty;
}
