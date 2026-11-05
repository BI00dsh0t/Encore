using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace ReplayBuffer.App.Settings;

/// <summary>
/// The Games list (story 10.10). View-only code: the Ignored group, opening a row on click or Enter, and
/// the drill-in's focus rules: an opened game's page takes focus on its first control, and going back
/// focuses the row it came from with the list's scroll restored.
/// </summary>
internal partial class GameProfilesView
{
    private double _listOffset;

    public GameProfilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GameProfilesPage old)
            {
                old.DrillIn.Opened -= OnOpened;
                old.DrillIn.Closed -= OnClosed;
            }

            if (e.NewValue is GameProfilesPage page)
            {
                var rows = CollectionViewSource.GetDefaultView(page.Profiles);
                rows.GroupDescriptions.Clear();
                rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameProfileItem.Group)));
                if (rows is ICollectionViewLiveShaping { CanChangeLiveGrouping: true } live)
                {
                    live.LiveGroupingProperties.Add(nameof(GameProfileItem.Group));
                    live.IsLiveGrouping = true;
                }
                page.DrillIn.Opened += OnOpened;
                page.DrillIn.Closed += OnClosed;
                if (page.OpenGame is { } open)
                {
                    Show(open);
                }
            }
        };
    }

    private GameProfilesPage? Page => DataContext as GameProfilesPage;

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GameList.SelectedItem is GameProfileItem item)
        {
            Page?.Open(item);
            e.Handled = true;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(GameList, hit) is ListBoxItem { DataContext: GameProfileItem item })
        {
            Page?.Open(item);
        }
    }

    private void OnOpened(string id)
    {
        if (Page?.OpenGame is { } game)
        {
            _listOffset = ListScroller.VerticalOffset;
            Show(game);
        }
    }

    private void Show(GamePage game)
    {
        ListScroller.Visibility = Visibility.Collapsed;
        GameHost.Content = game.View;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => game.View.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }

    private void OnClosed(string id)
    {
        GameHost.Content = null;
        ListScroller.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ListScroller.ScrollToVerticalOffset(_listOffset);
            if (Page?.Profiles.FirstOrDefault(p => p.ProfileId == id) is { } row)
            {
                GameList.SelectedItem = row;
                GameList.UpdateLayout();
                (GameList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
            }
        });
    }
}
