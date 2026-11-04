using System.Windows;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Theme;

/// <summary>
/// The shell's dictionaries (story 3): <see cref="ShellFluent"/> and <see cref="ShellGamingDark"/> define
/// the same keys, so the Main App's XAML names one key and gets the theme's look. <see cref="ThemeService"/>
/// places the theme's one in its merged list on each apply.
/// </summary>
internal static class ShellTheme
{
    /// <summary>
    /// Puts <paramref name="theme"/>'s shell dictionary in <paramref name="merged"/>, replacing the other
    /// theme's. Its keys are its own, so where it sits doesn't matter; it goes before the theme's dictionary
    /// so that one stays last.
    /// </summary>
    public static void Place(IList<ResourceDictionary> merged, AppTheme theme)
    {
        var at = IndexOfShell(merged);
        if (at >= 0 && merged[at] is ShellGamingDark == (theme == AppTheme.GamingDark))
        {
            return;
        }

        ResourceDictionary next = theme == AppTheme.GamingDark ? new ShellGamingDark() : new ShellFluent();
        if (at >= 0)
        {
            merged[at] = next;
        }
        else
        {
            merged.Insert(Math.Max(0, merged.Count - 1), next);
        }
    }

    private static int IndexOfShell(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ShellFluent or ShellGamingDark)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// A pressed state for nav items, which <see cref="System.Windows.Controls.ListBoxItem"/> lacks: with
/// <c>Track</c> on, <c>IsPressed</c> is true while the left button is down over the item.
/// </summary>
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

/// <summary>See <c>ShellFluent.xaml</c>.</summary>
internal partial class ShellFluent
{
    public ShellFluent() => InitializeComponent();
}

/// <summary>See <c>ShellGamingDark.xaml</c>.</summary>
internal partial class ShellGamingDark
{
    public ShellGamingDark() => InitializeComponent();
}
