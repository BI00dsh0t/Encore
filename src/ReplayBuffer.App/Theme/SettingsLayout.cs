using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ReplayBuffer.App.Theme;

/// <summary>See <c>SettingsLayout.xaml</c>.</summary>
internal partial class SettingsLayout
{
    public SettingsLayout() => InitializeComponent();

    /// <summary>
    /// Puts one <see cref="SettingsLayout"/> in <paramref name="merged"/> if it has none. Its keys are its
    /// own and it names the theme's only through DynamicResource, so it serves both themes; it goes before
    /// the last entry so the theme's dictionary stays last.
    /// </summary>
    public static void Place(IList<ResourceDictionary> merged)
    {
        if (merged.Any(d => d is SettingsLayout))
        {
            return;
        }

        merged.Insert(Math.Max(0, merged.Count - 1), new SettingsLayout());
    }
}

/// <summary>
/// The settings pages' content column (with <c>RbSettingsContentStyle</c>): a vertical stack that takes the
/// whole width it's offered up to its <see cref="FrameworkElement.MaxWidth"/>, while staying left-aligned.
/// A plain StackPanel left-aligned would shrink to its rows' text boxes, and stretched past its MaxWidth
/// WPF centres it.
/// </summary>
internal sealed class SettingsContent : StackPanel
{
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        return double.IsInfinity(constraint.Width) ? size : new Size(Math.Max(size.Width, constraint.Width), size.Height);
    }
}

/// <summary>Collapses an element whose bound text is null or empty; anything else is Visible.</summary>
internal sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
