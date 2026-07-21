using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class UserManagementViewModel : ObservableObject
{
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly IAuditSecurityLogger _auditLogger;

    public UserManagementViewModel(
        IAuthenticationAuthorizationService auth,
        IAuditSecurityLogger auditLogger)
    {
        _auth = auth;
        _auditLogger = auditLogger;
        Operators = new ObservableCollection<OperatorAccount>();
        AuditEntries = new ObservableCollection<SecurityAuditEntry>();
        RoleOptions = new ObservableCollection<string>(OperatorRoles.All);
        SelectedRole = OperatorRoles.Cashier;
        _ = RefreshAsync();
    }

    public ObservableCollection<OperatorAccount> Operators { get; }
    public ObservableCollection<SecurityAuditEntry> AuditEntries { get; }
    public ObservableCollection<string> RoleOptions { get; }

    [ObservableProperty]
    private OperatorAccount? _selectedOperator;

    [ObservableProperty]
    private string _newUsername = string.Empty;

    [ObservableProperty]
    private string _newDisplayName = string.Empty;

    [ObservableProperty]
    private string _selectedRole;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _resetPassword = string.Empty;

    [ObservableProperty]
    private bool _editIsActive = true;

    [ObservableProperty]
    private string _editDisplayName = string.Empty;

    [ObservableProperty]
    private string _editRole = OperatorRoles.Cashier;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private OperatorSession? _currentOperator;

    [ObservableProperty]
    private bool _isAuthenticated;

    partial void OnSelectedOperatorChanged(OperatorAccount? value)
    {
        if (value is null)
        {
            return;
        }

        EditDisplayName = value.DisplayName;
        EditRole = value.Role;
        EditIsActive = value.IsActive;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsAuthenticated = _auth.IsAuthenticated;
            CurrentOperator = _auth.CurrentOperator;
            Operators.Clear();
            foreach (var op in await _auth.GetOperatorsAsync().ConfigureAwait(true))
            {
                // Never expose hashes to the UI grid binding surface.
                op.PasswordHash = string.Empty;
                op.PasswordSalt = string.Empty;
                Operators.Add(op);
            }

            AuditEntries.Clear();
            foreach (var entry in await _auditLogger.GetRecentAsync(80).ConfigureAwait(true))
            {
                AuditEntries.Add(entry);
            }

            StatusMessage = $"Loaded {Operators.Count} operator(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        try
        {
            var result = await _auth.CreateOperatorAsync(NewUsername, NewDisplayName, SelectedRole, NewPassword)
                .ConfigureAwait(true);
            NewPassword = string.Empty;
            if (!result.Success)
            {
                StatusMessage = result.Error ?? "Create failed.";
                return;
            }

            NewUsername = string.Empty;
            NewDisplayName = string.Empty;
            StatusMessage = "Operator created.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveSelectedAsync()
    {
        if (SelectedOperator is null)
        {
            StatusMessage = "Select an operator first.";
            return;
        }

        try
        {
            var result = await _auth.UpdateOperatorAsync(
                    SelectedOperator.OperatorId,
                    EditDisplayName,
                    EditRole,
                    EditIsActive)
                .ConfigureAwait(true);
            StatusMessage = result.Success ? "Operator updated." : result.Error ?? "Update failed.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedOperator is null)
        {
            StatusMessage = "Select an operator first.";
            return;
        }

        try
        {
            var result = await _auth.ResetPasswordAsync(SelectedOperator.OperatorId, ResetPassword)
                .ConfigureAwait(true);
            ResetPassword = string.Empty;
            StatusMessage = result.Success ? "Password reset." : result.Error ?? "Reset failed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
