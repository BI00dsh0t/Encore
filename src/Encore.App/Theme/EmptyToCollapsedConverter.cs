using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Encore.App.Theme;

/// <summary>Collapses an element whose bound text is null or empty; anything else is Visible.</summary>
internal sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
