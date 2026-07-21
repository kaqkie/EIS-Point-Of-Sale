using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PointOfSale.App.Views;

public partial class HeadOfficeSyncView
{
    public HeadOfficeSyncView()
    {
        InitializeComponent();
    }
}

/// <summary>Inverts a bool for enabling Sync Now while idle.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
