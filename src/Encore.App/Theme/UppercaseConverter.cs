using System.Globalization;
using System.Windows.Data;

namespace Encore.App.Theme;

/// <summary>Uppercases text inside Gaming dark's templates, so view models and automation names keep sentence case.</summary>
internal sealed class UppercaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
