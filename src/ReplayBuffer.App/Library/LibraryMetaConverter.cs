using System.Globalization;
using System.Windows.Data;

namespace ReplayBuffer.App.Library;

/// <summary>The Library row's second line: the non-empty strings among game, date and size, joined with " · ".</summary>
internal sealed class LibraryMetaConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        string.Join(" · ", values.OfType<string>().Where(v => v.Length > 0));

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
