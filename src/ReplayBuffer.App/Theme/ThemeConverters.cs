using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ReplayBuffer.App.Theme;

/// <summary>
/// Uppercases string content inside Gaming dark's templates (DESIGN.md Typography), so view models,
/// XAML and automation names keep their sentence-case wording. Anything that isn't a string passes through.
/// </summary>
internal sealed class UppercaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Whether a level meter is in its top tenth (Value ≥ 0.9 of Maximum), where it turns <c>caution</c>.</summary>
internal sealed class LevelPeakConverter : IMultiValueConverter
{
    public const double Peak = 0.9;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double value, double maximum, ..] && maximum > 0 && value / maximum >= Peak;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element whose bound text is null or empty; anything else is Visible.</summary>
internal sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
