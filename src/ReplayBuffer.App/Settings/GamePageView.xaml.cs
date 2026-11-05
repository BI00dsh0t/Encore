using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace ReplayBuffer.App.Settings;

/// <summary>
/// A game's page (story 10.10). View-only code: the inline rename, where F2 focuses the name, Enter or
/// leaving commits, and Esc cancels (and is handled, so it doesn't also close the page).
/// </summary>
internal partial class GamePageView
{
    public GamePageView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GamePage old)
            {
                old.PropertyChanged -= OnPageChanged;
            }

            if (e.NewValue is GamePage page)
            {
                page.PropertyChanged += OnPageChanged;
                NameBox.Text = page.Name;
            }
        };
    }

    private GamePage? Page => DataContext as GamePage;

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamePage.Name) && !NameBox.IsKeyboardFocusWithin && Page is { } page)
        {
            NameBox.Text = page.Name;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            NameBox.Focus();
            NameBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (Page is not { } page)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            page.Name = NameBox.Text;
            NameBox.Text = page.Name;
            NameBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && NameBox.Text != page.Name)
        {
            NameBox.Text = page.Name;
            NameBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Page is { } page)
        {
            page.Name = NameBox.Text;
            NameBox.Text = page.Name;
        }
    }
}
