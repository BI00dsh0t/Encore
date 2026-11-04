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
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>The Library restyled (story 9.4): its rows, banner and empty state, in both themes.</summary>
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
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(which);
                window.Show();
                Sta.Pump(TimeSpan.FromMilliseconds(150));

                var view = (LibraryView)model.Library.View;
                Assert.True(view.IsLoaded, $"The Library didn't load under {which}");
                Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("Banner")).Visibility);

                var list = (ItemsControl)view.FindName("List");
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Single(Descendants<VirtualizingStackPanel>(list));

                var rows = model.Library.Rows.ToDictionary(r => r.Name);
                var ace = RowCard(list, rows["ace.mp4"]);
                Assert.Same(window.FindResource("RbLibraryRowStyle"), ace.Style);
                Assert.True(Descendants<ToggleButton>(ace).Single().IsChecked);
                Assert.False(Descendants<ToggleButton>(RowCard(list, rows["no-thumbnail.mp4"])).Single().IsChecked);
                Assert.Contains(Descendants<TextBlock>(RowCard(list, rows["saving.mp4"])), t => t.Text == "Saving…" && t.IsVisible);

                Save(window, $"library-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");
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
        var page = new LibraryPage(engine);
        var view = (LibraryView)page.View;
        Layout(view);
        var empty = (TextBlock)view.FindName("EmptyMessage");

        Assert.Equal(Visibility.Visible, empty.Visibility);
        page.FavouritesOnly = true;
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        page.FavouritesOnly = false;

        page.Render(engine.State! with { Library = [new LibraryItem("clip.mp4", null, 1024, Day, false, 10, Modified: Day)] });
        Layout(view);
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
        var star = Descendants<ToggleButton>((ItemsControl)view.FindName("List")).Single();
        Assert.False(star.IsChecked);

        // A click as the mouse makes it: ToggleButton.OnClick toggles, then runs the command.
        typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(star, null);

        Assert.Equal(new LibrarySetFavourite("clip.mp4", 1024, Day, true), Assert.Single(engine.Sent));
        Assert.False(star.IsChecked);

        // A screen reader's Toggle skips the click: it mustn't flip the star out of step with the snapshot.
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(star).GetPattern(PatternInterface.Toggle)).Toggle();
        Assert.False(star.IsChecked);
        page.Render(engine.State! with { Library = [new LibraryItem("clip.mp4", null, 1024, Day, false, 10, Favourite: true, Modified: Day)] });
        Assert.True(star.IsChecked);
    });

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(600, 400));
        view.Arrange(new Rect(0, 0, 600, 400));
        view.UpdateLayout();
    }

    private static Border RowCard(ItemsControl list, LibraryRow row)
    {
        var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromItem(row);
        return (Border)VisualTreeHelper.GetChild(container, 0);
    }

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
