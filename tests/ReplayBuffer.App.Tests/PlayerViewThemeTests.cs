using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>Story 14.2: the player page in the Main App window, in both looks, over a fake player.</summary>
[Collection(nameof(WpfCollection))]
public sealed class PlayerViewThemeTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-player-view-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ThePlayerPageShowsInBothLooksAndTheHiddenListIgnoresItsKeys() => Sta.Run(() =>
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            LibraryFolder = _folder,
            Library = [new LibraryItem(Clip, null, 1, DateTimeOffset.UnixEpoch, false)],
        };
        var failing = false;
        string? undecodable = null;
        FakePlayer? last = null;
        var model = new MainViewModel(engine, action => action(), player: () => last = undecodable is null
            ? new FakePlayer { Fail = failing }
            : new FakePlayer { Media = new PlayerMedia(1280, 720, TimeSpan.FromSeconds(10), ["Game"], false, undecodable) });
        var window = new MainWindow
        {
            DataContext = model,
            Width = 960,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        // One service for the window, as the App has: each ForWindow makes its own dictionaries, and a second
        // couldn't swap the first's out.
        var looks = ThemeService.ForWindow(window);
        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            window.Show();
            var page = model.Library;
            var row = page.Rows.Single();
            var view = (LibraryView)page.View;
            Sta.PumpUntil(() => view.IsLoaded, "the Library to load");
            foreach (var (theme, file) in new[] { (AppTheme.Fluent, "player-fluent.png"), (AppTheme.GamingDark, "player-gaming.png") })
            {
                looks.Apply(theme);
                page.Click(row, ctrl: false, shift: false);
                page.OpenFocused();
                Sta.Pump(TimeSpan.FromMilliseconds(150));

                var player = (PlayerView)view.FindName("PlayerContent");
                Assert.True(player.IsVisible);
                Assert.False(((FrameworkElement)view.FindName("ListContent")).IsVisible);
                Assert.True(model.IsDrilledIn);
                Assert.Contains(Descendants<Button>(player), b => b.Content as string == "Play" && b.IsVisible);

                // Story 14.5: the transport over the video, paused at 0:04 of the fake's 10 s file.
                page.Player!.SeekTo(TimeSpan.FromSeconds(4));
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(((FrameworkElement)player.FindName("SeekBar")).IsVisible);
                Assert.True(((FrameworkElement)player.FindName("VolumeBar")).IsVisible);
                Assert.Contains(Descendants<TextBlock>(player), t => t.Text == "0:04 / 0:10" && t.IsVisible);

                // Story 14.4: the Track list, by name, with the playing Track shown.
                var tracks = (ComboBox)player.FindName("TrackList");
                Assert.True(tracks.IsVisible);
                Assert.Equal("Game", ((PlayerTrack)tracks.SelectedItem).Name);
                Save(window, file);

                // The page's keys: Right seeks 5 s, Down lowers the volume 5 %, Space plays.
                foreach (var key in new[] { Key.Right, Key.Down, Key.Space })
                {
                    player.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                }

                Assert.Equal(TimeSpan.FromSeconds(9), last!.Seeks[^1]);
                Assert.Equal(0.95, Math.Round(last.Volume, 2));
                Assert.True(last.IsPlaying);
                page.Player.Volume = 1;

                // QA 14.4: with the Track list focused, its own arrows win, as a closed Windows ComboBox's do: Down and
                // Right pick the next Track, Up and Left the one before. They don't seek or change the volume there.
                tracks.Focus();
                var seeks = last.Seeks.Count;
                var picked = new List<int>();
                foreach (var key in new[] { Key.Down, Key.Right, Key.Left, Key.Up })
                {
                    tracks.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                    picked.Add(last.SelectedTrack);
                }

                Assert.Equal([1, 2, 1, 0], picked);
                Assert.Equal((seeks, 1.0), (last.Seeks.Count, last.Volume));

                // QA 14.4: the open drop-down, once in each look.
                tracks.IsDropDownOpen = true;
                Sta.Pump(TimeSpan.FromMilliseconds(200));
                SavePopup(tracks, file.Replace("player-", "player-tracks-", StringComparison.Ordinal));
                tracks.IsDropDownOpen = false;

                // The hidden list's keys do nothing: Space would star the row, F2 rename it.
                var list = (ListBox)view.FindName("List");
                foreach (var key in new[] { Key.Space, Key.F2 })
                {
                    list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }

                Assert.Empty(engine.Sent);
                Assert.False(row.IsRenaming);

                model.CurrentDrillIn!.Close();
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(((FrameworkElement)view.FindName("ListContent")).IsVisible);
            }

            // A file Windows can't play: the plain line and Open in default player.
            failing = true;
            page.Click(row, ctrl: false, shift: false);
            page.OpenFocused();
            Sta.Pump(TimeSpan.FromMilliseconds(150));
            var failed = (PlayerView)view.FindName("PlayerContent");
            Assert.Contains(Descendants<TextBlock>(failed), t => t.Text == PlayerPage.FailedText && t.IsVisible);
            Assert.Contains(Descendants<Button>(failed), b => b.Content as string == "Open in default player" && b.IsVisible);
            Save(window, "player-failed-gaming.png");
            model.CurrentDrillIn!.Close();

            // Story 14.6: a missing HEVC Video Extension, named, with its Store page, in both looks.
            (failing, undecodable) = (false, "HEVC");
            foreach (var (theme, file) in new[] { (AppTheme.Fluent, "player-codec-fluent.png"), (AppTheme.GamingDark, "player-codec-gaming.png") })
            {
                looks.Apply(theme);
                page.Click(row, ctrl: false, shift: false);
                page.OpenFocused();
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                var codec = (PlayerView)view.FindName("PlayerContent");
                Assert.Contains(Descendants<TextBlock>(codec), t => t.Text.Contains("HEVC Video Extension", StringComparison.Ordinal) && t.IsVisible);
                Assert.Contains(Descendants<Button>(codec), b => b.Content as string == "Get the HEVC Video Extension" && b.IsVisible);
                Assert.Contains(Descendants<Button>(codec), b => b.Content as string == "Open in default player" && b.IsVisible);
                Save(window, file);
                model.CurrentDrillIn!.Close();
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(((FrameworkElement)view.FindName("ListContent")).IsVisible);
            }

            // The player page's own bindings; WPF's ListBoxItem alignment noise from the hidden list is not this page's.
            Assert.DoesNotContain(errors.Messages, m => m.Contains("Player", StringComparison.Ordinal));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
            window.CloseForQuit();
        }
    });

    // Story 14.6, deferred to the 14.7 sweep: a page that gets no picture in time says the file can't be played.
    [Fact]
    public void APageWithNoPictureInTimeSaysTheFileCantBePlayed() => Sta.Run(() =>
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            LibraryFolder = _folder,
            Library = [new LibraryItem(Clip, null, 1, DateTimeOffset.UnixEpoch, false)],
        };
        var player = new FakePlayer();
        var model = new MainViewModel(engine, action => action(), player: () => player);
        var window = new MainWindow { DataContext = model, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var timeout = PlayerView.NoPictureTimeout;
        PlayerView.NoPictureTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            window.Show();
            var view = (LibraryView)model.Library.View;
            Sta.PumpUntil(() => view.IsLoaded, "the Library to load");
            model.Library.Click(model.Library.Rows.Single(), ctrl: false, shift: false);
            model.Library.OpenFocused();

            // QA 14.6: while the front buffer is gone (a lock screen) the timer waits; it restarts when it's back.
            var player2 = (PlayerView)view.FindName("PlayerContent");
            player2.FrontBufferChanged(false);
            Sta.Pump(TimeSpan.FromMilliseconds(700));
            Assert.False(model.Library.Player!.IsFailed);
            player2.FrontBufferChanged(true);

            // The fake never raises a frame.
            Sta.PumpUntil(() => model.Library.Player!.IsFailed, "the no-picture timeout");
            Assert.Equal(PlayerPage.FailedText, model.Library.Player!.Message);
            Assert.False(player.IsPlaying);
        }
        finally
        {
            PlayerView.NoPictureTimeout = timeout;
            window.CloseForQuit();
        }
    });

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

    // The ComboBox's drop-down is its own popup window: render its content.
    private static void SavePopup(ComboBox box, string name)
    {
        var popup = (System.Windows.Controls.Primitives.Popup)box.Template.FindName("PART_Popup", box);
        Assert.True(popup.IsOpen);
        var root = (FrameworkElement)popup.Child;
        var size = new Size(Math.Max(1, root.ActualWidth), Math.Max(1, root.ActualHeight));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }

    // The builder's hand check, beside the test binary.
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
