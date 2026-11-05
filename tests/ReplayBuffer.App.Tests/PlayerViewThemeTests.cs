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
        FakePlayer? last = null;
        var model = new MainViewModel(engine, action => action(), player: () => last = new FakePlayer { Fail = failing });
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
                ThemeService.ForWindow(window).Apply(theme);
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
