using System.Windows;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class SupervisorOverrideDialog : Window
{
    public SupervisorOverrideDialog(SupervisorOverrideViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool authorized)
    {
        DialogResult = authorized;
        Close();
    }
}
