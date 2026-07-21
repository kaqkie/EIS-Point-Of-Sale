using Microsoft.Extensions.DependencyInjection;

namespace PointOfSale.App.Services;

public interface INavigationService
{
    event EventHandler? CurrentViewModelChanged;
    object? CurrentViewModel { get; }
    void NavigateTo<TViewModel>() where TViewModel : class;
}

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public event EventHandler? CurrentViewModelChanged;

    public object? CurrentViewModel { get; private set; }

    public void NavigateTo<TViewModel>() where TViewModel : class
    {
        CurrentViewModel = _serviceProvider.GetRequiredService<TViewModel>();
        CurrentViewModelChanged?.Invoke(this, EventArgs.Empty);
    }
}
