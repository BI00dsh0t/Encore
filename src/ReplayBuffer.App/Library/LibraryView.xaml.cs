using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace ReplayBuffer.App.Library;

/// <summary>
/// The Library's view (story 10.2's input routing): the list's own selection is never used. A click, a
/// right-click and the keys go to the page's selection methods, and the view only scrolls the focused row
/// into view and puts keyboard focus on its container.
/// </summary>
internal partial class LibraryView
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private LibraryPage? Page => DataContext as LibraryPage;

    // Ctrl+F, anywhere on the page: focus the search box with its text selected (story 10.3). A rename box
    // keeps every key, so an open rename isn't committed by the focus moving away.
    private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control
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

    // The editor's keys stay in it: the list's own navigation never sees them. Only keys that make no text,
    // since a handled key down makes no text input.
    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            e.Handled = true;
        }
    }

    // A click on a row selects (Explorer's rules) or, double, opens; the star and the rename box keep theirs.
    // A click on a header does nothing, and one on the empty space clears the selection.
    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Page is not { } page || e.OriginalSource is not DependencyObject source || Within<ScrollBar>(source, List))
        {
            return;
        }

        if (ContainerOf(source) is not { } container)
        {
            page.ClearSelection();
            return;
        }

        e.Handled = true;
        if (container.DataContext is not LibraryRow row)
        {
            return;
        }

        if (Within<ButtonBase>(source, container) || Within<TextBox>(source, container))
        {
            e.Handled = false;
            return;
        }

        var (ctrl, shift) = Modifiers();
        page.Click(row, ctrl, shift);
        if (e.ClickCount == 2 && !ctrl && !shift)
        {
            page.OpenFocused();
        }

        container.Focus();
    }

    // Explorer's rule: a right-click outside the selection selects only that row first.
    // A right-click in the rename box is the box's own (its Cut, Copy and Paste menu), and leaves the edit open.
    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Page is { } page
            && e.OriginalSource is DependencyObject source
            && ContainerOf(source) is { DataContext: LibraryRow row } container
            && !Within<TextBox>(source, container))
        {
            page.PrepareContextMenu(row);
            container.Focus();
        }
    }

    // The menu acts on the shown selection, so it opens only on a row (or, from the keyboard, the focused row).
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Page is not { } page)
        {
            return;
        }

        if (e.OriginalSource is TextBox)
        {
            return;
        }

        var fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        if (fromKeyboard)
        {
            page.PrepareContextMenu(page.FocusedRow);
        }
        else if (e.OriginalSource is not DependencyObject source || ContainerOf(source) is not { DataContext: LibraryRow })
        {
            e.Handled = true;
            return;
        }

        if (!page.HasShownSelection)
        {
            e.Handled = true;
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The rename box takes every key; a focused button (the star) takes Space and Enter.
        if (Page is not { } page
            || e.OriginalSource is TextBox
            || (e.OriginalSource is ButtonBase && e.Key is Key.Space or Key.Enter))
        {
            return;
        }

        var (ctrl, shift) = Modifiers();
        switch (e.Key)
        {
            case Key.Up:
                Move(page, LibraryMove.Up, ctrl, shift);
                break;
            case Key.Down:
                Move(page, LibraryMove.Down, ctrl, shift);
                break;
            case Key.PageUp:
                Move(page, LibraryMove.PageUp, ctrl, shift);
                break;
            case Key.PageDown:
                Move(page, LibraryMove.PageDown, ctrl, shift);
                break;
            case Key.Home:
                Move(page, LibraryMove.Home, ctrl, shift);
                break;
            case Key.End:
                Move(page, LibraryMove.End, ctrl, shift);
                break;
            case Key.A when ctrl:
                page.SelectAll();
                break;
            case Key.Space when ctrl:
                page.ToggleFocusedSelection();
                break;
            case Key.Space:
                page.ToggleFocusedFavourite();
                break;
            case Key.Enter:
                page.OpenFocused();
                break;
            case Key.Delete:
                page.DeleteSelection();
                break;
            case Key.F2:
                page.RenameCommand.Execute(null);
                break;
            case Key.C when ctrl:
                page.CopyCommand.Execute(null);
                break;
            case Key.Escape:
                page.ClearSelection();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // Focus coming into the list from outside (Tab) onto the list or a container lands on the focused row,
    // else the first. Focus on an element inside a row (the star, the rename box) is left where it is.
    private void OnListGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Page is not { } page
            || (e.OldFocus is DependencyObject old && IsInList(old))
            || !(ReferenceEquals(e.NewFocus, List) || e.NewFocus is ListBoxItem))
        {
            return;
        }

        if (page.FocusedRow is null)
        {
            page.MoveFocus(LibraryMove.Home, 0, ctrl: true, shift: false);
        }

        if (page.FocusedRow is { } row && !ReferenceEquals((e.NewFocus as FrameworkElement)?.DataContext, row))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusRow(row));
        }
    }

    // The More ⋯ button opens its menu under itself.
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Move(LibraryPage page, LibraryMove move, bool ctrl, bool shift)
    {
        page.MoveFocus(move, RowsPerPage(), ctrl, shift);
        if (page.FocusedRow is { } row)
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
        var viewer = Descendant<ScrollViewer>(List);
        var row = Page?.FocusedRow is { } focused ? List.ItemContainerGenerator.ContainerFromItem(focused) as FrameworkElement : null;
        return viewer is null || row is not { ActualHeight: > 0 } ? 1 : Math.Max(1, (int)(viewer.ViewportHeight / row.ActualHeight));
    }

    private static (bool Ctrl, bool Shift) Modifiers() =>
        ((Keyboard.Modifiers & ModifierKeys.Control) != 0, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

    private ListBoxItem? ContainerOf(DependencyObject source) =>
        ItemsControl.ContainerFromElement(List, source) as ListBoxItem;

    private bool IsInList(DependencyObject element)
    {
        for (var d = element; d is not null; d = ParentOf(d))
        {
            if (ReferenceEquals(d, List))
            {
                return true;
            }
        }

        return false;
    }

    // Whether source is, or is inside, a T below top.
    private static bool Within<T>(DependencyObject source, DependencyObject top)
        where T : DependencyObject
    {
        for (var d = source; d is not null && !ReferenceEquals(d, top); d = ParentOf(d))
        {
            if (d is T)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
