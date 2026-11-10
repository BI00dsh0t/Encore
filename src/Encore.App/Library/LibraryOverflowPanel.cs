using System.Windows;
using System.Windows.Controls;

namespace Encore.App.Library;

/// <summary>The Library command bar's actions in a row; those that don't fit move into the More menu.</summary>
internal sealed class LibraryOverflowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(LibraryOverflowPanel), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OverflowsProperty = DependencyProperty.RegisterAttached(
        "Overflows", typeof(bool), typeof(LibraryOverflowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty IsMoreProperty = DependencyProperty.RegisterAttached(
        "IsMore", typeof(bool), typeof(LibraryOverflowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private bool _overflowing;

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetOverflows(UIElement element) => (bool)element.GetValue(OverflowsProperty);

    public static void SetOverflows(UIElement element, bool value) => element.SetValue(OverflowsProperty, value);

    public static bool GetIsMore(UIElement element) => (bool)element.GetValue(IsMoreProperty);

    public static void SetIsMore(UIElement element, bool value) => element.SetValue(IsMoreProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        _overflowing = RowWidth(overflowing: false) > availableSize.Width;
        foreach (UIElement child in InternalChildren)
        {
            if (GetIsMore(child))
            {
                Show(child, _overflowing);
            }
            else if (GetOverflows(child))
            {
                Show(child, !_overflowing);
            }
        }

        return new Size(Math.Min(RowWidth(_overflowing), availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (!Takes(child, _overflowing))
            {
                child.Arrange(new Rect(x, 0, 0, finalSize.Height));
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }

    // Whether the child takes space: never when collapsed; More only when overflowing, an overflow child only when not.
    private static bool Takes(UIElement child, bool overflowing) =>
        child.Visibility != Visibility.Collapsed
        && (GetIsMore(child) ? overflowing : !GetOverflows(child) || !overflowing);

    private double RowWidth(bool overflowing)
    {
        var (width, count) = (0.0, 0);
        foreach (UIElement child in InternalChildren)
        {
            if (Takes(child, overflowing))
            {
                width += child.DesiredSize.Width;
                count++;
            }
        }

        return width + (Math.Max(0, count - 1) * Spacing);
    }

    // Hidden, not collapsed, so the child is still measured; set only on a change, so layout settles.
    private static void Show(UIElement child, bool visible)
    {
        var wanted = visible ? Visibility.Visible : Visibility.Hidden;
        if (child.Visibility != Visibility.Collapsed && child.Visibility != wanted)
        {
            child.Visibility = wanted;
        }
    }
}
