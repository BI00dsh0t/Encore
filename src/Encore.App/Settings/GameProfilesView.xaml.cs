using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Encore.App.Settings;

/// <summary>The Games list, view-only code: the Ignored group, opening a row, and the drill-in's focus rules.</summary>
internal partial class GameProfilesView
{
    private double _listOffset;

    public GameProfilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GameProfilesViewModel old)
            {
                old.DrillIn.Opened -= OnOpened;
                old.DrillIn.Closed -= OnClosed;
            }

            if (e.NewValue is GameProfilesViewModel viewModel)
            {
                var rows = CollectionViewSource.GetDefaultView(viewModel.Profiles);
                rows.GroupDescriptions.Clear();
                rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameProfileItem.Group)));
                if (rows is ICollectionViewLiveShaping { CanChangeLiveGrouping: true } live)
                {
                    live.LiveGroupingProperties.Add(nameof(GameProfileItem.Group));
                    live.IsLiveGrouping = true;
                }
                viewModel.DrillIn.Opened += OnOpened;
                viewModel.DrillIn.Closed += OnClosed;
                if (viewModel.OpenGame is { } open)
                {
                    Show(open);
                }
            }
        };
    }

    private GameProfilesViewModel? ViewModel => DataContext as GameProfilesViewModel;

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GameList.SelectedItem is GameProfileItem item)
        {
            ViewModel?.Open(item);
            e.Handled = true;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(GameList, hit) is ListBoxItem { DataContext: GameProfileItem item })
        {
            ViewModel?.Open(item);
        }
    }

    private void OnOpened(string id)
    {
        if (ViewModel?.OpenGame is { } game)
        {
            _listOffset = ListScroller.VerticalOffset;
            Show(game);
        }
    }

    private void Show(GameViewModel game)
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
            if (ViewModel?.Profiles.FirstOrDefault(profile => profile.ProfileId == id) is { } row)
            {
                GameList.SelectedItem = row;
                GameList.UpdateLayout();
                (GameList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
            }
        });
    }
}
