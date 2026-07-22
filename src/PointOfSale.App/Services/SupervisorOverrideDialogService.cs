using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.App.ViewModels;
using PointOfSale.App.Views;

namespace PointOfSale.App.Services;

/// <summary>
/// Shows the modal supervisor override dialog from cashier workflows.
/// </summary>
public interface ISupervisorOverrideDialogService
{
    Task<SupervisorAuthorizationResult> PromptAsync(
        SupervisorOverrideRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SupervisorOverrideDialogService : ISupervisorOverrideDialogService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SupervisorOverrideDialogService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public Task<SupervisorAuthorizationResult> PromptAsync(
        SupervisorOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(
                SupervisorAuthorizationResult.Denied("Supervisor dialog requires an active WPF dispatcher."));
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowDialog(request));
        }

        return dispatcher.InvokeAsync(() => ShowDialog(request)).Task;
    }

    private SupervisorAuthorizationResult ShowDialog(SupervisorOverrideRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<SupervisorOverrideViewModel>();
        viewModel.Configure(request);

        var dialog = new SupervisorOverrideDialog(viewModel);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        var accepted = dialog.ShowDialog() == true;
        if (accepted && viewModel.LastResult is { Authorized: true } granted)
        {
            return granted;
        }

        return viewModel.LastResult
               ?? SupervisorAuthorizationResult.Denied("Authorization cancelled by operator.");
    }
}
