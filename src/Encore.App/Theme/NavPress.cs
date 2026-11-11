using System.Windows;

namespace Encore.App.Theme;

/// <summary>A pressed state for nav items, which ListBoxItem lacks: IsPressed is true while the left button is down.</summary>
internal static class NavPress
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(NavPress), new PropertyMetadata(false, OnTrackChanged));

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(NavPress), new PropertyMetadata(false));

    public static bool GetTrack(DependencyObject element) => (bool)element.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject element, bool value) => element.SetValue(TrackProperty, value);

    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);

    public static void SetIsPressed(DependencyObject element, bool value) => element.SetValue(IsPressedProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += Press;
            element.PreviewMouseLeftButtonUp += Release;
            element.MouseLeave += Release;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= Press;
            element.PreviewMouseLeftButtonUp -= Release;
            element.MouseLeave -= Release;
            element.ClearValue(IsPressedProperty);
        }
    }

    private static void Press(object sender, System.Windows.Input.MouseButtonEventArgs e) => SetIsPressed((DependencyObject)sender, true);

    private static void Release(object sender, System.Windows.Input.MouseEventArgs e) => SetIsPressed((DependencyObject)sender, false);
}
