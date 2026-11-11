using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Encore.App.Settings;

namespace Encore.App;

/// <summary>The Settings window, owned by the Main App window. Closing it only hides it; Quit closes it for good.</summary>
internal partial class SettingsWindow
{
    private bool _quitting;

    public SettingsWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Model?.IsShown = IsVisible;
    }

    internal override bool UsesMica => true;

    /// <summary>Restores and saves the window's size and place; null in tests, which place the window themselves.</summary>
    internal WindowPlacementKeeper? Placement { get; init; }

    private SettingsWindowViewModel? Model => DataContext as SettingsWindowViewModel;

    /// <summary>Shows the window and brings it to the front, with focus on its section list the first time.</summary>
    public void Open()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (!IsKeyboardFocusWithin)
        {
            FocusSelectedSection();
        }
    }

    /// <summary>Closes the window for good, on Quit.</summary>
    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        Placement?.Restore(this);
        base.OnSourceInitialized(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Placement?.Save(this);
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    // Esc goes back from an item page, else closes the window; Alt+Left goes back; Ctrl+Tab steps through the sections.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (key == Key.Escape)
        {
            CloseUnlessWentBack(model);
        }
        else if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            model.GoBack();
        }
        else if (key == Key.Tab && control)
        {
            model.StepBy((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            FocusSelectedSection();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void CloseUnlessWentBack(SettingsWindowViewModel model)
    {
        if (!model.GoBack())
        {
            Close();
        }
    }

    // The mouse's Back button closes an item page.
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && Model?.GoBack() == true)
        {
            e.Handled = true;
        }
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

    private void FocusSelectedSection()
    {
        Sections.UpdateLayout();
        (Sections.ItemContainerGenerator.ContainerFromItem(Sections.SelectedItem) as ListBoxItem)?.Focus();
    }
}
