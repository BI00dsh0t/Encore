using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Encore.App.Library;

/// <summary>The Library's view: the list's own selection is unused; clicks and keys go to the page's selection methods.</summary>
internal partial class LibraryView
{
    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

    // The list is hidden while the player page shows, so Delete can't reach the file being played.
    private LibraryViewModel? ListViewModel => ViewModel is { DrillIn.IsOpen: false } viewModel ? viewModel : null;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LibraryViewModel old)
        {
            old.DrillIn.Opened -= OnPlayerOpened;
            old.DrillIn.Closed -= OnPlayerClosed;
        }

        if (e.NewValue is LibraryViewModel viewModel)
        {
            viewModel.DrillIn.Opened += OnPlayerOpened;
            viewModel.DrillIn.Closed += OnPlayerClosed;
        }
    }

    // Ctrl+F focuses the search box; an open rename box keeps every key, so the focus move doesn't commit it.
    private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ListViewModel is not null
            && e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control
            && (e.OriginalSource is not TextBox box || ReferenceEquals(box, SearchBox)))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // Esc in the search box clears it at once, even within the 150 ms the search waits for typing.
    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            ClearSearch();
            e.Handled = true;
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchBox.Focus();
    }

    // Empties the box and pushes it to the page now, past the binding's delay.
    private void ClearSearch()
    {
        SearchBox.Text = "";
        SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    // The inline editor takes focus with its text selected once shown; after the context menu has closed.
    private void OnRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && e.NewValue is true)
        {
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    // Leaving the editor commits it; a no-op once Enter or Escape has ended the edit.
    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryRow row })
        {
            row.CommitRenameCommand.Execute(null);
        }
    }

    // The editor's keys stay in it, so the list's navigation never sees them.
    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            e.Handled = true;
        }
    }

    // A click on a row selects or, double, opens; a header does nothing, and empty space clears the selection.
    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ListViewModel is not { } viewModel
            || e.OriginalSource is not DependencyObject source
            || VisualTreeSearch.IsInside<ScrollBar>(source, List))
        {
            return;
        }

        if (ContainerOf(source) is not { } container)
        {
            viewModel.ClearSelection();
            return;
        }

        e.Handled = true;
        if (container.DataContext is not LibraryRow row)
        {
            return;
        }

        if (VisualTreeSearch.IsInside<ButtonBase>(source, container) || VisualTreeSearch.IsInside<TextBox>(source, container))
        {
            e.Handled = false;
            return;
        }

        var (ctrl, shift) = Modifiers();
        viewModel.ClickRow(row, e.ClickCount, ctrl, shift);
        container.Focus();
    }

    // Explorer's rule: a right-click outside the selection selects only that row; the rename box keeps its own menu.
    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ListViewModel is { } viewModel
            && e.OriginalSource is DependencyObject source
            && ContainerOf(source) is { DataContext: LibraryRow row } container
            && !VisualTreeSearch.IsInside<TextBox>(source, container))
        {
            viewModel.PrepareContextMenu(row);
            container.Focus();
        }
    }

    // The menu acts on the shown selection, so it opens only on a row (or, from the keyboard, the focused row).
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ListViewModel is not { } viewModel)
        {
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is TextBox)
        {
            return;
        }

        // A pointer reports where it opened the menu; the keyboard reports -1, -1.
        var fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        var onRow = e.OriginalSource is DependencyObject source && ContainerOf(source) is { DataContext: LibraryRow };
        if (!viewModel.TryPrepareContextMenu(fromKeyboard, onRow))
        {
            e.Handled = true;
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The rename box takes every key; a focused button (the star) takes Space and Enter.
        if (ListViewModel is not { } viewModel
            || e.OriginalSource is TextBox
            || (e.OriginalSource is ButtonBase && e.Key is Key.Space or Key.Enter))
        {
            return;
        }

        var (ctrl, shift) = Modifiers();
        if (MoveFor(e.Key) is { } move)
        {
            Move(viewModel, move, ctrl, shift);
            e.Handled = true;
        }
        else if (viewModel.HandleKey(e.Key, ctrl))
        {
            e.Handled = true;
        }
    }

    // The keys that move the focused row; the view handles them, as a page's length is the viewport's.
    private static LibraryMove? MoveFor(Key key) => key switch
    {
        Key.Up => LibraryMove.Up,
        Key.Down => LibraryMove.Down,
        Key.PageUp => LibraryMove.PageUp,
        Key.PageDown => LibraryMove.PageDown,
        Key.Home => LibraryMove.Home,
        Key.End => LibraryMove.End,
        _ => null,
    };

    // Focus tabbing into the list lands on the focused row, else the first; focus inside a row is left alone.
    private void OnListGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel
            || (e.OldFocus is DependencyObject old && VisualTreeSearch.IsInside(old, List))
            || !(ReferenceEquals(e.NewFocus, List) || e.NewFocus is ListBoxItem))
        {
            return;
        }

        if (viewModel.FocusForEntry() is { } row && !ReferenceEquals((e.NewFocus as FrameworkElement)?.DataContext, row))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusRow(row));
        }
    }

    // The player page opened: its first control takes focus once it's shown.
    private void OnPlayerOpened(string path) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ViewModel?.Creator is not null)
            {
                CreatorContent.FocusFirstControl();
            }
            else
            {
                PlayerContent.FocusFirstControl();
            }
        });

    // Back on the list: focus returns to the row that was played, as Games does.
    private void OnPlayerClosed(string path) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ViewModel?.FocusedRow is { } row)
            {
                FocusRow(row);
            }
        });

    // The More ⋯ button opens its menu under itself.
    private void OnMoreClick(object sender, RoutedEventArgs e) => ButtonMenu.OpenBelow(sender);

    private void Move(LibraryViewModel viewModel, LibraryMove move, bool ctrl, bool shift)
    {
        viewModel.MoveFocus(move, RowsPerPage(), ctrl, shift);
        if (viewModel.FocusedRow is { } row)
        {
            FocusRow(row);
        }
    }

    private void FocusRow(LibraryRow row)
    {
        List.ScrollIntoView(row);
        List.UpdateLayout();
        (List.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    // How many rows a page holds: the viewport's height over a realized row's.
    private int RowsPerPage()
    {
        var viewer = VisualTreeSearch.Descendant<ScrollViewer>(List);
        var row = ViewModel?.FocusedRow is { } focused ? List.ItemContainerGenerator.ContainerFromItem(focused) as FrameworkElement : null;
        return viewer is null || row is not { ActualHeight: > 0 } ? 1 : Math.Max(1, (int)(viewer.ViewportHeight / row.ActualHeight));
    }

    private static (bool Ctrl, bool Shift) Modifiers() =>
        ((Keyboard.Modifiers & ModifierKeys.Control) != 0, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

    private ListBoxItem? ContainerOf(DependencyObject source) =>
        ItemsControl.ContainerFromElement(List, source) as ListBoxItem;
}
