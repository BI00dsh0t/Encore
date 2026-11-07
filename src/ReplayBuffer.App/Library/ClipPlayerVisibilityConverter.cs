using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ReplayBuffer.App.Library;

/// <summary>Creator transport replaces source scrub/time controls without taking ownership of player styles.</summary>
internal sealed class ClipPlayerVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is true && values[1] is not true ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
