using System.Windows;
using System.Windows.Media;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class EnterpriseMaintenanceView
{
    private EnterpriseMaintenanceViewModel? _viewModel;

    public EnterpriseMaintenanceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as EnterpriseMaintenanceViewModel;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnRendering(object? sender, EventArgs e) => _viewModel?.NotifyUiFrameRendered();
}
