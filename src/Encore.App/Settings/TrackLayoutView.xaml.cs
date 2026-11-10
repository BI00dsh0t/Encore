using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Encore.App.Settings;

/// <summary>Audio Tracks' view, view-only code: the menus, anchoring the sound picker, the picker's keys and Rename focus.</summary>
internal partial class TrackLayoutView
{
    public TrackLayoutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => GroupPickerRows();
    }

    private TrackLayoutViewModel? ViewModel => DataContext as TrackLayoutViewModel;

    private static bool IsControlDown => Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    // The sound picker lists its rows under their section headings.
    private void GroupPickerRows()
    {
        if (ViewModel is { } viewModel)
        {
            var rows = CollectionViewSource.GetDefaultView(viewModel.Picker.Rows);
            rows.GroupDescriptions.Clear();
            rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AudioPickerRow.Section)));
        }
    }

    /// <summary>Drops the root ScrollViewer when hosted inside another page: a nested one swallows the mouse wheel.</summary>
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

    // A track's ⋯ button and the Presets button open their menus under themselves.
    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => ButtonMenu.OpenBelow(sender);

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: AudioPresetChoice choice })
        {
            ViewModel?.ApplyPresetCommand.Execute(choice.Preset);
        }
    }

    private void OnAddSoundClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioTrackCard card } button && ViewModel is { } viewModel)
        {
            Picker.PlacementTarget = button;
            viewModel.Picker.Open(card);
            Dispatcher.BeginInvoke(() => PickerSearchBox.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnPickerClosed(object? sender, EventArgs e) => ViewModel?.Picker.Close();

    // Down moves from the search box into the list; Enter and Escape work as they do in the list.
    private void OnPickerSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && PickerList.Items.Count > 0)
        {
            FocusSelectedRow();
            e.Handled = true;
        }
        else if (HandlePickerKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnPickerListKeyDown(object sender, KeyEventArgs e)
    {
        if (HandlePickerKey(e.Key))
        {
            e.Handled = true;
        }
    }

    // Enter adds the selected row (with Ctrl the picker stays open); Escape closes the picker.
    private bool HandlePickerKey(Key key)
    {
        if (key == Key.Enter && ViewModel?.Picker.Selection is { } row)
        {
            ViewModel.Picker.Pick(row, IsControlDown);
            return true;
        }

        if (key == Key.Escape)
        {
            ViewModel?.Picker.Close();
            return true;
        }

        return false;
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
            && ViewModel is { } viewModel)
        {
            viewModel.Picker.Pick(row, IsControlDown);
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
