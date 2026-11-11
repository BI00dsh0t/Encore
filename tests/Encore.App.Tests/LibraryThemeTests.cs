using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Library;
using Encore.App.Theme;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The Library restyled and selectable: its rows, banner, empty state, selection and command bar, in both themes.</summary>
[Collection(nameof(WpfCollection))]
public sealed class LibraryThemeTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheLibraryLoadsInBothThemesWithItsBannerLockAndFavourite() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            Warnings = [new Warning("w-corrupt", ErrorCodes.LibraryCorrupt, WarningSeverity.Warning, "library", "rebuilt", DateTimeOffset.UnixEpoch, false, null, true)],
            CleanupBlocked = true,
            Library =
            [
                new LibraryItem(@"Valorant\ace.mp4", null, 48L << 20, Day, false, 30, Favourite: true, Modified: Day),
                new LibraryItem(@"Valorant\saving.mp4", null, 0, Day.AddMinutes(5), true),
                new LibraryItem(@"Desktop\no-thumbnail.mp4", null, 3L << 20, Day.AddDays(-1), false, 95, Modified: Day),
            ],
        };
        var model = new MainViewModel(engine, action => action());
        var window = new MainWindow
        {
            DataContext = model,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        var theme = ThemeService.ForWindow(window);
        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var viewModel = model.Library;
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                var name = which == AppTheme.Fluent ? "fluent" : "gaming-dark";
                viewModel.Selection.Clear();
                theme.Apply(which);
                window.Show();
                Sta.Pump(TimeSpan.FromMilliseconds(150));

                var view = (LibraryView)viewModel.View;
                Sta.PumpUntil(() => view.IsLoaded, $"the Library to load under {which}");
                Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("Banner")).Visibility);
                var bar = (FrameworkElement)view.FindName("CommandBar");
                Assert.Equal((true, 40.0), (bar.IsVisible, bar.ActualHeight));

                var list = (ListBox)view.FindName("List");
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Single(Descendants<VirtualizingStackPanel>(list));

                var rows = viewModel.List.Rows.ToDictionary(r => r.Name);
                var ace = RowCard(list, rows["ace.mp4"]);
                Assert.Same(window.FindResource("EncoreLibraryRowStyle"), ace.Style);
                Assert.True(Descendants<ToggleButton>(ace).Single().IsChecked);
                Assert.False(Descendants<ToggleButton>(RowCard(list, rows["no-thumbnail.mp4"])).Single().IsChecked);
                Assert.Contains(Descendants<TextBlock>(RowCard(list, rows["saving.mp4"])), t => t.Text == "Saving…" && t.IsVisible);

                Save(window, $"library-{name}.png");

                // Two rows selected, the second focused, with keyboard focus in the list.
                viewModel.Selection.Click(rows["ace.mp4"], ctrl: false, shift: false);
                viewModel.Selection.Click(rows["no-thumbnail.mp4"], ctrl: true, shift: false);
                window.Activate();
                Container(list, rows["no-thumbnail.mp4"]).Focus();
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                Sta.PumpUntil(() => Descendants<TextBlock>(bar).Any(t => t.Text == "2 selected" && t.IsVisible), "\"2 selected\" in the command bar");

                Assert.Equal((true, 40.0), (bar.IsVisible, bar.ActualHeight));
                var selected = (SolidColorBrush)window.FindResource("EncoreLibraryRowSelectedBrush");
                Assert.Equal((selected.Color, selected.Opacity), Fill(RowCard(list, rows["ace.mp4"])));
                Assert.NotEqual(Fill(RowCard(list, rows["saving.mp4"])), Fill(RowCard(list, rows["ace.mp4"])));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Single(Descendants<VirtualizingStackPanel>(list));

                Save(window, $"library-selected-{name}.png");

                // A search with Largest first and Group by None, nothing selected.
                viewModel.Selection.Clear();
                (viewModel.List.GroupBy, viewModel.List.Sort, viewModel.List.SearchText) = (LibraryGrouping.None, LibrarySort.LargestFirst, "a");
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                Assert.Equal("a", ((TextBox)view.FindName("SearchBox")).Text);
                Assert.Equal(["ace.mp4", "no-thumbnail.mp4", "saving.mp4"], viewModel.List.Entries.Cast<LibraryRow>().Select(r => r.Name));
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("NoResultsMessage")).Visibility);
                Save(window, $"library-search-{name}.png");
                (viewModel.List.GroupBy, viewModel.List.Sort, viewModel.List.SearchText) = (LibraryGrouping.Game, LibrarySort.NewestFirst, "");
                list.ScrollIntoView(viewModel.List.Entries[0]);
            }
        }
        finally
        {
            window.CloseForQuit();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        Assert.DoesNotContain(errors.Messages, m => m.Contains("Library", StringComparison.Ordinal));
    });

    [Fact]
    public void TheEmptyStateShowsOnlyWithoutAFilterAndTheStarFollowsOnlyTheSnapshot() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession { Reply = (_, _) => Empty.Value };
        var viewModel = new LibraryViewModel(engine);
        var view = (LibraryView)viewModel.View;
        Layout(view);
        var empty = (FrameworkElement)view.FindName("EmptyMessage");

        // Until the engine's first snapshot the Library knows nothing, so neither the welcome nor the command bar shows.
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("CommandBar")).Visibility);
        viewModel.Render(engine.State!);
        Assert.Equal(Visibility.Visible, empty.Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("CommandBar")).Visibility);
        viewModel.List.FavouritesOnly = true;
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        viewModel.List.FavouritesOnly = false;

        viewModel.Render(engine.State! with { Library = [new LibraryItem("clip.mp4", null, 1024, Day, false, 10, Modified: Day)] });
        Layout(view);
        Assert.Equal(Visibility.Collapsed, empty.Visibility);

        // A search that matches nothing says so, apart from the empty Library's message.
        var noResults = (TextBlock)view.FindName("NoResultsMessage");
        Assert.Equal(Visibility.Collapsed, noResults.Visibility);
        viewModel.List.SearchText = "zzz";
        Layout(view);
        Assert.Equal((Visibility.Visible, "No items match \u201czzz\u201d.", Visibility.Collapsed), (noResults.Visibility, noResults.Text, empty.Visibility));
        viewModel.List.SearchText = "";
        Layout(view);

        var star = Descendants<ToggleButton>((ItemsControl)view.FindName("List")).Single();
        Assert.False(star.IsChecked);

        // A click as the mouse makes it: ToggleButton.OnClick toggles, then runs the command.
        typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(star, null);

        Assert.Equal(new LibrarySetFavourite("clip.mp4", 1024, Day, true), Assert.Single(engine.Sent));
        Assert.False(star.IsChecked);

        // A screen reader's Toggle skips the click: it mustn't flip the star out of step with the snapshot.
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(star).GetPattern(PatternInterface.Toggle)).Toggle();
        Assert.False(star.IsChecked);
        viewModel.Render(engine.State! with { Library = [new LibraryItem("clip.mp4", null, 1024, Day, false, 10, Favourite: true, Modified: Day)] });
        Assert.True(star.IsChecked);
    });

    [Fact]
    public void TheFirstUseTipsTheEmptyLineABadgedRowAndTheStatusHintLoadInBothThemes() => Sta.Run(() =>
    {
        // The tips over an empty Library, then a Recording's badge and the header's hint.
        var engine = new FakeEngineSession { Reply = (_, _) => Empty.Value };
        var settings = SettingsDocument.Empty with
        {
            Global = SettingsMaps.EmptyValues
                .Add(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(30))
                .Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(
                    new HotkeyBinding(0x79, HotkeyModifiers.Alt), null, new HotkeyBinding(0x78, HotkeyModifiers.Alt), null, null, null))),
        };
        engine.State = engine.State! with { Settings = settings, LibraryFolder = @"C:\Users\sam\Videos\Encore" };
        var firstUseFolder = Path.Combine(Path.GetTempPath(), "encore-first-use-" + Guid.NewGuid().ToString("N"));
        var model = new MainViewModel(engine, action => action(), firstUseFile: Path.Combine(firstUseFolder, "first-use.json"));
        var window = new MainWindow { DataContext = model, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var theme = ThemeService.ForWindow(window);
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                var name = which == AppTheme.Fluent ? "fluent" : "gaming-dark";
                engine.State = engine.State! with { Library = [], Capture = CaptureState.Initial };
                engine.RaiseChanged();
                theme.Apply(which);
                window.Show();
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                var view = (LibraryView)model.Library.View;
                Sta.PumpUntil(() => view.IsLoaded, $"the Library to load under {which}");
                Assert.True(((FrameworkElement)view.FindName("EmptyMessage")).IsVisible);
                Assert.True(((FrameworkElement)view.FindName("Tips")).IsVisible);
                Assert.False(((FrameworkElement)view.FindName("CommandBar")).IsVisible);
                Save(window, $"library-tips-{name}.png");

                engine.State = engine.State with
                {
                    Seq = engine.State.Seq + 1,
                    TargetGame = new TargetGame(@"c:\g\valorant.exe", "Valorant", 7),
                    Capture = CaptureState.Initial with { Replay = ReplayState.Buffering, MicMuted = false },
                    Library = [new LibraryItem(@"Valorant\match.mp4", null, 48L << 20, Day, false, 1800, Modified: Day, Kind: LibraryItemKinds.Recording)],
                };
                engine.RaiseChanged();
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.False(((FrameworkElement)view.FindName("EmptyMessage")).IsVisible);
                Assert.Equal("· Alt+F10 saves the last 30 s", model.StatusLine.Hint);
                Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Recording" && t.IsVisible);
                Save(window, $"library-badge-hint-{name}.png");
            }
        }
        finally
        {
            window.CloseForQuit();
            Directory.CreateDirectory(firstUseFolder);
            Directory.Delete(firstUseFolder, recursive: true);
        }
    });

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(600, 400));
        view.Arrange(new Rect(0, 0, 600, 400));
        view.UpdateLayout();
    }

    // The row's card: the first child of its template's root, inside the ListBoxItem container.
    private static Border RowCard(ListBox list, LibraryRow row)
    {
        var root = (Grid)VisualTreeHelper.GetChild(Descendants<ContentPresenter>(Container(list, row))[0], 0);
        return (Border)root.Children[0];
    }

    // The row's ListBoxItem once it's realised with its template applied; under load that can take longer than a fixed pump.
    private static ListBoxItem Container(ListBox list, LibraryRow row)
    {
        ListBoxItem? container = null;
        Sta.PumpUntil(
            () =>
            {
                list.UpdateLayout();
                container = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
                return container is not null && Descendants<ContentPresenter>(container) is [var presenter, ..] && VisualTreeHelper.GetChildrenCount(presenter) > 0;
            },
            $"the row container for {row.Name}");
        return container!;
    }

    private static (Color Color, double Opacity) Fill(Border card) =>
        card.Background is SolidColorBrush brush ? (brush.Color, brush.Opacity) : default;

    private static List<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Descendants<T>(child));
        }

        return found;
    }

    // The builder's hand check: the Library as it renders, beside the test binary.
    private static void Save(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }
}
