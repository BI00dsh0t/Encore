using System.Windows;
using System.Windows.Input;

namespace ReplayBuffer.App.Settings;

internal partial class HotkeysView
{
    public HotkeysView()
    {
        InitializeComponent();
    }

    // Capture ends when the builder leaves the page, so a later key can't bind a stale row.
    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as HotkeysPage)?.CancelCapture();

    private void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            (DataContext as HotkeysPage)?.CancelCapture();
        }
    }

    // While a row captures, the next key press is its combination; Escape cancels.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeysPage { CapturingRow: { } row } page)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            page.CancelCapture();
            return;
        }

        if (HotkeysPage.FromKey(e.Key, e.SystemKey, Keyboard.Modifiers) is { } binding)
        {
            page.Capture(row.Action, binding);
        }
    }
}
