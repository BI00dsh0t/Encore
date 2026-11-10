using System.Globalization;
using System.Windows.Data;

namespace Encore.App.Theme;

/// <summary>Whether a level meter is in its top tenth (Value ≥ 0.9 of Maximum), where it turns <c>caution</c>.</summary>
internal sealed class LevelPeakConverter : IMultiValueConverter
{
    public const double Peak = 0.9;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double value, double maximum, ..] && maximum > 0 && value / maximum >= Peak;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
