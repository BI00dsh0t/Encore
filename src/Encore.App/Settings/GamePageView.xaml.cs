using System.Windows.Input;

namespace Encore.App.Settings;

/// <summary>A game's page, view-only code: F2 focuses the name, and the name box's keys go to the view model's draft.</summary>
internal partial class GamePageView
{
    public GamePageView() => InitializeComponent();

    private GameViewModel? ViewModel => DataContext as GameViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            NameBox.Focus();
            NameBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnNameGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsEditingName = true;
        }
    }

    // Enter commits the name and Esc puts it back; an Esc with nothing to put back is left for the page to close.
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            viewModel.CommitName();
        }
        else if (e.Key != Key.Escape || !viewModel.RevertName())
        {
            return;
        }

        NameBox.SelectAll();
        e.Handled = true;
    }

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsEditingName = false;
            viewModel.CommitName();
        }
    }
}
