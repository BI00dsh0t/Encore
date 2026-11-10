using System.Windows;
using System.Windows.Controls;

namespace Encore.App.Theme;

/// <summary>See <c>SettingsLayout.xaml</c>.</summary>
internal partial class SettingsLayout
{
    public SettingsLayout() => InitializeComponent();
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
