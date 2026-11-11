using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Encore.App.Settings;

namespace Encore.App;

/// <summary>Settings over the Main App window: it shows and hides with its view model's open state, and keeps focus inside.</summary>
internal partial class SettingsOverlay
{
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(150);
    private const double ClosedScale = 0.96;

    // What had focus when the overlay opened, so closing gives it back.
    private IInputElement? _returnFocus;

    public SettingsOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private SettingsOverlayViewModel? Model => DataContext as SettingsOverlayViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsOverlayViewModel old)
        {
            old.PropertyChanged -= OnModelPropertyChanged;
        }

        if (e.NewValue is SettingsOverlayViewModel model)
        {
            model.PropertyChanged += OnModelPropertyChanged;
            ShowOpenState(model.IsOpen);
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsOverlayViewModel.IsOpen) && Model is { } model)
        {
            ShowOpenState(model.IsOpen);
        }
    }

    private void ShowOpenState(bool open)
    {
        if (open)
        {
            Appear();
        }
        else
        {
            Disappear();
        }
    }

    // A short fade and scale in, then focus on the selected section once the panel is laid out.
    private void Appear()
    {
        if (Visibility != Visibility.Visible)
        {
            _returnFocus = Keyboard.FocusedElement;
            Opacity = 0;
            Visibility = Visibility.Visible;
        }

        Animate(1, 1, then: null);
        Dispatcher.BeginInvoke(FocusSelectedSection, DispatcherPriority.Loaded);
    }

    // The fade out ends hidden; focus goes back to what opened the overlay once the window behind takes input again.
    private void Disappear()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        Animate(0, ClosedScale, then: () => Visibility = Visibility.Collapsed);
        var target = _returnFocus;
        _returnFocus = null;
        Dispatcher.BeginInvoke(() => target?.Focus(), DispatcherPriority.Input);
    }

    // Windows' "Animation effects" off makes every change instant.
    private void Animate(double opacity, double scale, Action? then)
    {
        var duration = SystemParameters.ClientAreaAnimation ? FadeTime : TimeSpan.Zero;
        var fade = new DoubleAnimation(opacity, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (then is not null)
        {
            // A reopen during the fade out keeps the overlay shown.
            fade.Completed += (_, _) => FinishClosing(then);
        }

        if (opacity > 0)
        {
            CardScale.ScaleX = ClosedScale;
            CardScale.ScaleY = ClosedScale;
        }

        BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration));
    }

    private void FinishClosing(Action then)
    {
        if (Model?.IsOpen != true)
        {
            then();
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        Model?.Close();
        e.Handled = true;
    }

    // Ctrl+Tab and Ctrl+Shift+Tab step through the sections; Esc and Back are the Main App window's, which asks the view model.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || Model is not { } model)
        {
            return;
        }

        model.StepBy((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        FocusSelectedSection();
        e.Handled = true;
    }

    // Choosing the shown section again goes back to its list.
    private void OnSectionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(Sections, hit) is ListBoxItem { DataContext: NavItem section })
        {
            Model?.ReselectSection(section);
        }
    }

    // Enter or Space on the shown section does what a click on it does.
    private void OnSectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.OriginalSource is ListBoxItem { DataContext: NavItem section } && Model is { } model)
        {
            model.ReselectSection(section);
            e.Handled = true;
        }
    }

    // A narrow panel shows the sections as icons; the content is clipped to the panel's rounded corners.
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var asRail = NavPaneLayout.SettingsSectionsAsRail(e.NewSize.Width);
        SectionsColumn.Width = new GridLength(asRail ? NavPaneLayout.RailWidth : NavPaneLayout.SettingsSectionsWidth);
        Sections.Tag = asRail ? NavPaneLayout.RailTag : null;
        var radius = Math.Max(0, Panel.CornerRadius.TopLeft - Panel.BorderThickness.Left);
        PanelContent.Clip = new RectangleGeometry(new Rect(PanelContent.RenderSize), radius, radius);
    }

    private void FocusSelectedSection()
    {
        Sections.UpdateLayout();
        (Sections.ItemContainerGenerator.ContainerFromItem(Sections.SelectedItem) as ListBoxItem)?.Focus();
    }
}
