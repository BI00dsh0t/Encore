using System.Windows;
using System.Windows.Controls;

namespace Encore.App.Library;

/// <summary>Measure compact controls first, then give the picture the finite viewport's remaining height.</summary>
internal sealed class ClipCreatorPanel : Panel
{
    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(nameof(ViewportHeight), typeof(double), typeof(ClipCreatorPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ViewportHeight { get => (double)GetValue(ViewportHeightProperty); set => SetValue(ViewportHeightProperty, value); }
    private double _playerHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var controlsHeight = 0d;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            if (index == 1) { continue; }
            InternalChildren[index].Measure(new Size(width, double.PositiveInfinity));
            controlsHeight += InternalChildren[index].DesiredSize.Height;
        }
        // A short window scrolls the entire minimum editor, including wrapped controls.
        _playerHeight = Math.Max(300, ViewportHeight - controlsHeight);
        if (InternalChildren.Count > 1) { InternalChildren[1].Measure(new Size(width, _playerHeight)); }
        return new Size(width, controlsHeight + _playerHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0d;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var height = index == 1 ? _playerHeight : InternalChildren[index].DesiredSize.Height;
            InternalChildren[index].Arrange(new Rect(0, top, finalSize.Width, height));
            top += height;
        }
        return finalSize;
    }
}
