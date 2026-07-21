using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IConnectionStatusService _connectionStatusService;

    public MainViewModel(INavigationService navigationService, IConnectionStatusService connectionStatusService)
    {
        _navigationService = navigationService;
        _connectionStatusService = connectionStatusService;
        _navigationService.CurrentViewModelChanged += (_, _) => CurrentViewModel = _navigationService.CurrentViewModel;
        _connectionStatusService.StatusChanged += (_, _) => RefreshConnectionState();

        RefreshConnectionState();
        _navigationService.NavigateTo<CheckoutViewModel>();
    }

    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isMraReachable;

    [ObservableProperty]
    private string _connectionStatusText = string.Empty;

    [ObservableProperty]
    private bool _isDrawerOpen = true;

    [RelayCommand]
    private void NavigateCheckout() => _navigationService.NavigateTo<CheckoutViewModel>();

    [RelayCommand]
    private void NavigateInventory() => _navigationService.NavigateTo<InventoryViewModel>();

    [RelayCommand]
    private void NavigateQueue() => _navigationService.NavigateTo<QueueSyncStatusViewModel>();

    [RelayCommand]
    private void ToggleDrawer() => IsDrawerOpen = !IsDrawerOpen;

    [RelayCommand]
    private async Task RefreshConnectionAsync() =>
        await _connectionStatusService.RefreshAsync().ConfigureAwait(true);

    private void RefreshConnectionState()
    {
        IsOnline = _connectionStatusService.IsOnline;
        IsMraReachable = _connectionStatusService.IsMraReachable;
        ConnectionStatusText = _connectionStatusService.StatusText;
    }
}
