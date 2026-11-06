using System.Windows;
using System.Windows.Controls;

namespace ReplayBuffer.App.Library;

/// <summary>Wraps complete time groups, placing duration at the trailing edge when space permits.</summary>
internal sealed class ClipRangePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); }
        return Layout(availableSize.Width, false);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }
    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width) { y += rowHeight; x = 0; rowHeight = 0; }
            var left = i == InternalChildren.Count - 1 && double.IsFinite(width) ? Math.Max(x, width - size.Width) : x;
            if (arrange) { child.Arrange(new Rect(left, y, size.Width, size.Height)); }
            x = left + size.Width;
            usedWidth = Math.Max(usedWidth, x);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(usedWidth, y + rowHeight);
    }
}
