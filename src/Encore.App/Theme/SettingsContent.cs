using System.Windows;
using System.Windows.Controls;

namespace Encore.App.Theme;

/// <summary>The settings pages' content column: takes the width offered up to its MaxWidth, while staying left-aligned.</summary>
internal sealed class SettingsContent : StackPanel
{
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        return double.IsInfinity(constraint.Width) ? size : new Size(Math.Max(size.Width, constraint.Width), size.Height);
    }
}
