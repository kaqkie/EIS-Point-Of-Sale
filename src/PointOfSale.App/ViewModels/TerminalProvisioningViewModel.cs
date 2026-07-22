using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using Microsoft.Extensions.Options;

namespace PointOfSale.App.ViewModels;

public partial class TerminalProvisioningViewModel : ObservableObject
{
    private readonly ITerminalProvisioningService _provisioning;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly TerminalDeploymentOptions _deployment;

    public TerminalProvisioningViewModel(
        ITerminalProvisioningService provisioning,
        IAuthenticationAuthorizationService auth,
        IOptions<TerminalDeploymentOptions> deployment)
    {
        _provisioning = provisioning;
        _auth = auth;
        _deployment = deployment.Value;
        BranchIdInput = _deployment.BranchId;
        SiteIdInput = _deployment.SiteId;
        _ = RefreshStateAsync();
    }

    [ObservableProperty]
    private string _terminalIdInput = string.Empty;

    [ObservableProperty]
    private string _taxpayerTinInput = string.Empty;

    [ObservableProperty]
    private string _activationStatus = "Prepare local deployment, then activate with MRA EIS.";

    [ObservableProperty]
    private bool _isProvisioned;

    [ObservableProperty]
    private string _branchIdInput = string.Empty;

    [ObservableProperty]
    private string _siteIdInput = string.Empty;

    [ObservableProperty]
    private string _terminalActivationCode = string.Empty;

    [ObservableProperty]
    private string _hardwareFingerprint = string.Empty;

    [ObservableProperty]
    private bool _hardwareBindingValid = true;

    [ObservableProperty]
    private bool _sqlExpressReachable;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task RefreshStateAsync()
    {
        try
        {
            var state = await _provisioning.GetStateAsync().ConfigureAwait(true);
            IsProvisioned = state.IsProvisioned;
            ActivationStatus = state.ActivationStatus ?? string.Empty;
            HardwareFingerprint = state.HardwareFingerprintSha256;
            HardwareBindingValid = state.HardwareBindingValid;
            SqlExpressReachable = state.SqlExpressReachable;

            if (!string.IsNullOrWhiteSpace(state.ActiveTerminalId))
            {
                TerminalIdInput = state.ActiveTerminalId;
            }

            if (!string.IsNullOrWhiteSpace(state.TaxpayerTin))
            {
                TaxpayerTinInput = state.TaxpayerTin;
            }
        }
        catch (Exception ex)
        {
            ActivationStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PrepareDeploymentAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _auth.EnsurePermission(OperatorPermissions.ProvisionTerminal);
            var result = await _provisioning.PrepareLocalDeploymentAsync().ConfigureAwait(true);
            ActivationStatus = result.Message;
            await RefreshStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ActivationStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ActivateMraTerminalAsync()
    {
        if (IsBusy || IsProvisioned)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _auth.EnsurePermission(OperatorPermissions.ProvisionTerminal);

            if (string.IsNullOrWhiteSpace(TerminalActivationCode))
            {
                ActivationStatus = "Enter the MRA terminal activation code (TAC).";
                return;
            }

            var result = await _provisioning.ActivateWithMraAsync(
                    new TerminalProvisioningRequest
                    {
                        TerminalActivationCode = TerminalActivationCode,
                        BranchId = BranchIdInput,
                        SiteId = SiteIdInput,
                        TaxpayerTin = TaxpayerTinInput,
                        TerminalIdInput = string.IsNullOrWhiteSpace(TerminalIdInput)
                            ? null
                            : TerminalIdInput.Trim()
                    })
                .ConfigureAwait(true);

            ActivationStatus = result.Message;
            if (result.Success && !string.IsNullOrWhiteSpace(result.TerminalId))
            {
                TerminalIdInput = result.TerminalId;
                TerminalActivationCode = string.Empty;
            }

            await RefreshStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ActivationStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
