using System.Windows;
using System.Windows.Controls;

namespace Encore.App.Library;

/// <summary>Keep transport at the card's true center; wrap complete controls when the side groups cannot fit.</summary>
internal sealed class ClipTransportPanel : Panel
{
    private int _rows;
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 1000;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(width, double.PositiveInfinity)); }
        if (InternalChildren.Count != 3) { return new Size(width, 0); }
        var time = InternalChildren[0].DesiredSize;
        var transport = InternalChildren[1].DesiredSize;
        var preview = InternalChildren[2].DesiredSize;
        _rows = width >= 2 * Math.Max(time.Width, preview.Width) + transport.Width + 16 ? 1
            : width >= time.Width + transport.Width + 8 ? 2 : 3;
        return new Size(width, _rows == 1 ? Math.Max(38, Math.Max(time.Height, Math.Max(transport.Height, preview.Height))) : _rows == 2 ? 76 : 110);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 3) { return finalSize; }
        var width = finalSize.Width;
        var time = InternalChildren[0]; var transport = InternalChildren[1]; var preview = InternalChildren[2];
        void Place(UIElement child, double x, double y, double rowHeight) => child.Arrange(new Rect(Math.Max(0, x), y + Math.Max(0, (rowHeight - child.DesiredSize.Height) / 2), Math.Min(width, child.DesiredSize.Width), child.DesiredSize.Height));
        Place(time, 0, 0, 38);
        Place(transport, _rows == 1 ? (width - transport.DesiredSize.Width) / 2 : width - transport.DesiredSize.Width, _rows == 3 ? 36 : 0, 38);
        Place(preview, width - preview.DesiredSize.Width, _rows == 1 ? 0 : _rows == 2 ? 38 : 72, 38);
        return finalSize;
    }
}
