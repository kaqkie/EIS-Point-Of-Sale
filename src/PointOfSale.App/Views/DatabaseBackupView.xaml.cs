using System.Windows.Controls;
using System.Windows.Input;
using PointOfSale.App.Services;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class DatabaseBackupView
{
    public DatabaseBackupView()
    {
        InitializeComponent();
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DatabaseBackupViewModel vm)
        {
            return;
        }

        if (sender is DataGrid grid && grid.SelectedItem is DatabaseBackupHistoryEntry entry)
        {
            if (vm.UseHistoryBackupCommand.CanExecute(entry))
            {
                vm.UseHistoryBackupCommand.Execute(entry);
            }
        }
    }
}
