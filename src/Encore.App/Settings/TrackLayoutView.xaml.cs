using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Encore.App.Settings;

/// <summary>
/// Audio Tracks' view (story 10.5). The code here is view-only: opening the ⋯ and preset menus on a
/// click, anchoring the sound picker to the pressed + Add sound, the picker's keys (↑ ↓ Enter Esc, typing
/// to search, Ctrl+click to keep it open) and focusing a card's name box for Rename.
/// </summary>
internal partial class TrackLayoutView
{
    public TrackLayoutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TrackLayoutPage page)
            {
                var rows = CollectionViewSource.GetDefaultView(page.PickerRows);
                rows.GroupDescriptions.Clear();
                rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AudioPickerRow.Section)));
            }
        };
    }

    private TrackLayoutPage? Page => DataContext as TrackLayoutPage;

    /// <summary>
    /// Drops the root ScrollViewer for a page hosted inside another page's view (a game's own tracks):
    /// a nested ScrollViewer never scrolls there but swallows the mouse wheel.
    /// </summary>
    public void Unscroll()
    {
        if (Scroller.Content is UIElement content)
        {
            Scroller.Content = null;
            Root.Children.Remove(Scroller);
            Grid.SetRow(content, 1);
            Root.Children.Add(content);
        }
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private void OnPresetButtonClick(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private static void OpenMenu(object sender)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: AudioPresetChoice choice })
        {
            Page?.ApplyPresetCommand.Execute(choice.Preset);
        }
    }

    private void OnAddSoundClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioTrackCard card } button && Page is { } page)
        {
            Picker.PlacementTarget = button;
            page.OpenPicker(card);
            Dispatcher.BeginInvoke(() => PickerSearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnPickerClosed(object? sender, EventArgs e) => Page?.ClosePicker();

    private void OnPickerSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when PickerList.Items.Count > 0:
                FocusSelectedRow();
                e.Handled = true;
                break;
            case Key.Enter when Page?.PickerSelection is { } row:
                Page.Pick(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
            case Key.Escape:
                Page?.ClosePicker();
                e.Handled = true;
                break;
        }
    }

    private void OnPickerListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Page?.PickerSelection is { } row:
                Page.Pick(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
            case Key.Escape:
                Page?.ClosePicker();
                e.Handled = true;
                break;
        }
    }

    // Typing in the list goes to the search box.
    private void OnPickerListTextInput(object sender, TextCompositionEventArgs e)
    {
        PickerSearchBox.Focus();
        PickerSearchBox.SelectedText = e.Text;
        PickerSearchBox.CaretIndex = PickerSearchBox.Text.Length;
        e.Handled = true;
    }

    // A click adds the row and closes the picker; Ctrl+click adds it and keeps the picker open.
    private void OnPickerListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(PickerList, hit) is ListBoxItem { DataContext: AudioPickerRow row, IsEnabled: true }
            && Page is { } page)
        {
            page.Pick(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        }
    }

    private void FocusSelectedRow()
    {
        var item = PickerList.SelectedItem ?? PickerList.Items[0];
        PickerList.SelectedItem = item;
        if (PickerList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
        {
            container.Focus();
        }
    }

    private void OnNameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: AudioTrackCard card } box)
        {
            card.RenameRequested -= RenameHandler(box);
            card.RenameRequested += RenameHandler(box);
            if (card.TakePendingRename())
            {
                Focus(box);
            }
        }
    }

    private void OnNameBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: AudioTrackCard card } box)
        {
            card.RenameRequested -= RenameHandler(box);
        }
    }

    // One handler per box, so Loaded and Unloaded add and remove the same delegate.
    private static Action RenameHandler(TextBox box)
    {
        if (box.Tag is not Action handler)
        {
            handler = () => Focus(box);
            box.Tag = handler;
        }

        return handler;
    }

    private static void Focus(TextBox box)
    {
        box.Focus();
        box.SelectAll();
        box.BringIntoView();
    }
}
