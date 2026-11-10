using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Encore.App.Theme;

/// <summary>Collapsed for true, Visible otherwise: the shell's page title while a drill-in item page shows its breadcrumb.</summary>
internal sealed class TrueToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
