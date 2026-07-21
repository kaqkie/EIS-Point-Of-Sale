using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

public partial class ComplianceExportViewModel : ObservableObject
{
    private readonly IComplianceCertificationService _certificationService;
    private readonly IComplianceExportService _exportService;

    public ComplianceExportViewModel(
        IComplianceCertificationService certificationService,
        IComplianceExportService exportService)
    {
        _certificationService = certificationService;
        _exportService = exportService;
        StatusLog = new ObservableCollection<string>();
        StatusMessage = "Ready to run MRA EIS certification and export the compliance package.";
    }

    public ObservableCollection<string> StatusLog { get; }

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _lastPackagePath = string.Empty;

    [ObservableProperty]
    private string _overallResult = string.Empty;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCertificationAndExportAsync()
    {
        IsExporting = true;
        StatusLog.Clear();
        Append("Starting MRA EIS certification suite...");

        try
        {
            var progress = new Progress<string>(Append);
            var document = await _certificationService
                .RunCertificationAsync(progress)
                .ConfigureAwait(true);

            OverallResult = document.OverallResult;
            StatusMessage = $"Certification {document.OverallResult}: {document.Steps.Count(s => s.Passed)}/{document.Steps.Count} scenarios passed.";
            Append(StatusMessage);

            Append("Compressing compliance package (audit + schema + reports)...");
            var export = await _exportService
                .ExportPackageAsync(document.TerminalId)
                .ConfigureAwait(true);

            if (!export.Success)
            {
                StatusMessage = $"Export failed: {export.Message}";
                Append(StatusMessage);
                return;
            }

            LastPackagePath = export.PackagePath;
            StatusMessage = $"Package ready: {export.PackagePath}";
            Append(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Certification failed: {ex.Message}";
            Append(StatusMessage);
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ExportOnlyAsync()
    {
        IsExporting = true;
        Append("Exporting existing audit package...");

        try
        {
            var export = await _exportService.ExportPackageAsync().ConfigureAwait(true);
            if (export.Success)
            {
                LastPackagePath = export.PackagePath;
                StatusMessage = $"Package ready: {export.PackagePath}";
            }
            else
            {
                StatusMessage = $"Export failed: {export.Message}";
            }

            Append(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Append(StatusMessage);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanRun() => !IsExporting;

    private void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = message.StartsWith('[')
            ? message
            : $"[{DateTime.Now:HH:mm:ss}] {message}";
        StatusLog.Add(line);
    }

    partial void OnIsExportingChanged(bool value)
    {
        RunCertificationAndExportCommand.NotifyCanExecuteChanged();
        ExportOnlyCommand.NotifyCanExecuteChanged();
    }
}
