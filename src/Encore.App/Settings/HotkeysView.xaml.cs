using System.Windows;
using System.Windows.Input;

namespace Encore.App.Settings;

internal partial class HotkeysView
{
    public HotkeysView()
    {
        InitializeComponent();
    }

    // Capture ends when the builder leaves the page, so a later key can't bind a stale row.
    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as HotkeysViewModel)?.CancelCapture();

    private void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            (DataContext as HotkeysViewModel)?.CancelCapture();
        }
    }

    // While a row captures, the next key press is its combination; Escape cancels.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeysViewModel { CapturingRow: { } row } viewModel)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            viewModel.CancelCapture();
            return;
        }

        if (HotkeysViewModel.FromKey(e.Key, e.SystemKey, Keyboard.Modifiers) is { } binding)
        {
            viewModel.Capture(row.Action, binding);
        }
    }
}
