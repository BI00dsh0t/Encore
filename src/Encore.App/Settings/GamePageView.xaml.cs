using System.ComponentModel;
using System.Windows.Input;

namespace Encore.App.Settings;

/// <summary>A game's page, view-only code: F2 focuses the name, Enter or leaving commits, and Esc cancels the rename.</summary>
internal partial class GamePageView
{
    public GamePageView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GameViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }

            if (e.NewValue is GameViewModel viewModel)
            {
                viewModel.PropertyChanged += OnViewModelChanged;
                NameBox.Text = viewModel.Name;
            }
        };
    }

    private GameViewModel? ViewModel => DataContext as GameViewModel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.Name) && !NameBox.IsKeyboardFocusWithin && ViewModel is { } viewModel)
        {
            NameBox.Text = viewModel.Name;
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
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            viewModel.Name = NameBox.Text;
            NameBox.Text = viewModel.Name;
            NameBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && NameBox.Text != viewModel.Name)
        {
            NameBox.Text = viewModel.Name;
            NameBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Name = NameBox.Text;
            NameBox.Text = viewModel.Name;
        }
    }
}
