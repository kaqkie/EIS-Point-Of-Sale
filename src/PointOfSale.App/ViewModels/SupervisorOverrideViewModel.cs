using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Modal supervisor override dialog — masked PIN/password entry for restricted cashier actions.
/// </summary>
public partial class SupervisorOverrideViewModel : ObservableObject
{
    private readonly ISupervisorAuthorizationService _authorization;

    public SupervisorOverrideViewModel(ISupervisorAuthorizationService authorization)
    {
        _authorization = authorization;
    }

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    private string _title = "Supervisor authorization required";

    [ObservableProperty]
    private string _message = "A supervisor must authorize this restricted action.";

    [ObservableProperty]
    private string _actionType = SupervisorOverrideActions.ItemVoid;

    [ObservableProperty]
    private string _requiredPermission = OperatorPermissions.PerformVoid;

    [ObservableProperty]
    private string? _reason;

    [ObservableProperty]
    private string _supervisorUsername = string.Empty;

    [ObservableProperty]
    private string _credential = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAuthorized;

    public SupervisorAuthorizationResult? LastResult { get; private set; }

    public void Configure(SupervisorOverrideRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = request.ActionType;
        RequiredPermission = request.RequiredPermission;
        Reason = request.Reason;
        Title = $"Authorize: {request.ActionType}";
        Message = string.IsNullOrWhiteSpace(request.Reason)
            ? $"Permission '{request.RequiredPermission}' is required. Enter a supervisor username + password, or a dedicated override PIN."
            : request.Reason!;
        SupervisorUsername = request.SupervisorUsername ?? string.Empty;
        Credential = string.Empty;
        StatusMessage = string.Empty;
        IsAuthorized = false;
        LastResult = null;
    }

    [RelayCommand]
    private async Task AuthorizeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Validating supervisor credentials…";
        try
        {
            var result = await _authorization.AuthorizeAsync(
                    new SupervisorOverrideRequest
                    {
                        ActionType = ActionType,
                        RequiredPermission = RequiredPermission,
                        Reason = Reason,
                        SupervisorUsername = string.IsNullOrWhiteSpace(SupervisorUsername)
                            ? null
                            : SupervisorUsername.Trim(),
                        Credential = Credential,
                        AllowCurrentSession = false
                    })
                .ConfigureAwait(true);

            LastResult = result;
            IsAuthorized = result.Authorized;
            if (result.Authorized)
            {
                StatusMessage = result.Message ?? "Authorized.";
                Credential = string.Empty;
                CloseRequested?.Invoke(this, true);
            }
            else
            {
                StatusMessage = result.Error ?? "Authorization denied.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastResult = SupervisorAuthorizationResult.Denied(ex.Message);
            IsAuthorized = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Credential = string.Empty;
        LastResult = SupervisorAuthorizationResult.Denied("Authorization cancelled by operator.");
        IsAuthorized = false;
        CloseRequested?.Invoke(this, false);
    }
}
