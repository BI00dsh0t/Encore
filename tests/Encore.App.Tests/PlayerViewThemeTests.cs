using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Library;
using Encore.App.Theme;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The player page in the Main App window, in both looks, over a fake player.</summary>
[Collection(nameof(WpfCollection))]
public sealed class PlayerViewThemeTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-player-view-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // The one focused render allowed for the blank-controls bug; no media, engine or GPU surface is used.
    [Fact]
    public void ThePlayerIconsRenderInBothLooks() => Sta.Run(() =>
    {
        using var page = new PlayerPage("clip.mp4", "clip", new FakePlayer(), openRead: _ => new MemoryStream([0]));
        page.OpenAsync().GetAwaiter().GetResult();
        var player = new PlayerView { DataContext = page };
        player.FrontBufferChanged(false);
        var window = new Window
        {
            Content = player, Width = 720, Height = 400,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        var looks = ThemeService.ForWindow(window);
        try
        {
            foreach (var (theme, file) in new[] { (AppTheme.Fluent, "player-icons-fluent.png"), (AppTheme.GamingDark, "player-icons-gaming.png") })
            {
                looks.Apply(theme);
                // Reproduce an app-level text style overriding inherited button typography in either look.
                var textStyle = new Style(typeof(TextBlock));
                textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI")));
                window.Resources[typeof(TextBlock)] = textStyle;
                window.Show();
                window.UpdateLayout();
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                CheckIcons(theme);
                Save(window, file);

                page.PlayPauseCommand.Execute(null);
                page.MuteCommand.Execute(null);
                page.FullScreenCommand.Execute(null);
                window.UpdateLayout();
                CheckIcons(theme);
                page.PlayPauseCommand.Execute(null);
                page.MuteCommand.Execute(null);
                page.FullScreenCommand.Execute(null);
            }
        }
        finally
        {
            window.Close();
        }

        void CheckIcons(AppTheme theme)
        {
            foreach (var name in new[] { "PlayPause", "Mute", "Gear", "FullScreen" })
            {
                var button = (ButtonBase)player.FindName(name);
                Assert.True(button.IsVisible);
                var glyph = Assert.IsType<TextBlock>(button.Template.FindName("Glyph", button));
                Assert.Equal(button.Content, glyph.Text);
                Assert.Equal(button.FontFamily, glyph.FontFamily);
                Assert.Equal(button.FontSize, glyph.FontSize);
                Assert.Equal(button.Foreground, glyph.Foreground);
                Assert.True(new Typeface(new FontFamily(glyph.FontFamily.Source.Split(',')[0]), glyph.FontStyle, glyph.FontWeight, glyph.FontStretch)
                    .TryGetGlyphTypeface(out var face));
                Assert.True(face.CharacterToGlyphMap.TryGetValue(glyph.Text[0], out var index) && index != 0,
                    $"{theme}: {name} must draw a real icon, not a missing-glyph rectangle.");
                Assert.True(Assert.IsType<SolidColorBrush>(glyph.Foreground).Color.A > 0);
                Assert.True(glyph.ActualWidth > 0 && glyph.ActualHeight > 0);
            }
        }
    });

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
        // One service for the window, as the App has: a second couldn't swap the first's dictionaries out.
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
                var play = (Button)player.FindName("PlayPause");
                Assert.Equal(("Play", true), (AutomationProperties.GetName(play), play.IsVisible));

                // Resting: the seek bar over one row; the volume slider is hidden until the speaker is hovered or focused.
                page.Player!.SeekTo(TimeSpan.FromSeconds(4));
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(((FrameworkElement)player.FindName("SeekBar")).IsVisible);
                Assert.True(((FrameworkElement)player.FindName("Gear")).IsVisible);
                Assert.True(((FrameworkElement)player.FindName("FullScreen")).IsVisible);
                Assert.False(player.IsVolumeShown);
                Assert.Contains(Descendants<TextBlock>(player), t => t.Text == "0:04 / 0:10" && t.IsVisible);
                Save(window, file);

                // The volume slider slides out on hover, and on keyboard focus.
                var speaker = (FrameworkElement)player.FindName("VolumeGroup");
                speaker.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                Assert.True(player.IsVolumeShown);
                speaker.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                Assert.False(player.IsVolumeShown);
                ((Button)player.FindName("Mute")).Focus();
                Assert.True(player.IsVolumeShown);
                play.Focus();
                Assert.False(player.IsVolumeShown);

                // The page's keys: Right seeks 5 s, Down lowers the volume 5 %, Space plays.
                foreach (var key in new[] { Key.Right, Key.Down, Key.Space })
                {
                    player.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                }

                Assert.Equal(TimeSpan.FromSeconds(9), last!.Seeks[^1]);
                Assert.Equal(0.95, Math.Round(last.Volume, 2));
                Assert.True(last.IsPlaying);
                page.Player.Volume = 1;

                // The gear's menu: the heading isn't a choice; the Tracks and the last item take arrow-key focus.
                var menu = player.OpenSettingsMenu()!;
                Sta.Pump(TimeSpan.FromMilliseconds(200));
                Assert.Same(menu, player.SettingsMenu);
                Assert.True(((ToggleButton)player.FindName("Gear")).IsChecked);
                var items = menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal("Audio track", ((TextBlock)items[0].Header).Text);
                Assert.Equal(TextAlignment.Center, ((TextBlock)items[0].Header).TextAlignment);
                Assert.False(items[0].Focusable);
                Assert.Equal(["Game", "Mic", "Discord", "Desktop"], items.Skip(1).Take(4).Select(i => (string)i.Header));
                Assert.Equal([true, false, false, false], items.Skip(1).Take(4).Select(i => i.IsChecked));
                Assert.Single(menu.Items.OfType<Separator>());
                Assert.Equal("Open in default player", items[^1].Header);
                Assert.All(items.Skip(1), i => Assert.True(i.Focusable));
                var check = Assert.IsType<System.Windows.Shapes.Path>(items[1].Template.FindName("Check", items[1]));
                Assert.True(check.IsVisible);
                Assert.Equal(1, Grid.GetColumn(check));
                Assert.Same(items[1].FindResource("AccentTextFillColorPrimaryBrush"), check.Stroke);
                SaveMenu(menu, file.Replace("player-", "player-menu-", StringComparison.Ordinal));

                // A choice switches Track and closes the menu.
                ((IInvokeProvider)new MenuItemAutomationPeer(items[3])).Invoke();
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                Assert.Equal(2, last.SelectedTrack);
                Assert.Null(player.SettingsMenu);
                Assert.False(((ToggleButton)player.FindName("Gear")).IsChecked);

                // The hidden list's keys do nothing: Space would star the row, F2 rename it.
                var list = (ListBox)view.FindName("List");
                foreach (var key in new[] { Key.Space, Key.F2 })
                {
                    list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }

                Assert.Empty(engine.Sent);
                Assert.False(row.IsRenaming);

                // Full screen: F enters it, Esc leaves it with the page still open, and closing the page leaves it.
                var pageHost = (FrameworkElement)window.FindName("Page");
                var margin = new Thickness(3, 15, 7, 9);
                pageHost.Margin = margin;
                player.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.F) { RoutedEvent = Keyboard.KeyDownEvent });
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                Assert.True(page.Player!.IsFullScreen);
                Assert.True(window.IsFullScreen);
                Assert.Equal((WindowStyle.None, Visibility.Collapsed), (window.WindowStyle, ((FrameworkElement)window.FindName("NavPane")).Visibility));
                Save(window, file.Replace("player-", "player-fullscreen-", StringComparison.Ordinal));
                player.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.False(window.IsFullScreen);
                Assert.Equal(margin, pageHost.Margin);
                Assert.True(model.IsDrilledIn);
                ((IInvokeProvider)new ButtonAutomationPeer((Button)player.FindName("FullScreen"))).Invoke();
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(window.IsFullScreen);

                // Alt+F4 uses the same close-to-tray path. Reopening keeps the page, with the frame restored.
                window.Close();
                Assert.False(window.IsVisible);
                Assert.False(window.IsFullScreen);
                Assert.False(page.Player.IsFullScreen);
                Assert.Equal(margin, pageHost.Margin);
                window.Show();
                Assert.True(model.IsDrilledIn);
                Assert.False(window.IsFullScreen);
                page.Player.FullScreenCommand.Execute(null);

                model.CurrentDrillIn!.Close();
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(((FrameworkElement)view.FindName("ListContent")).IsVisible);
                Assert.False(window.IsFullScreen);
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

            // A missing HEVC Video Extension, named, with its Store page, in both looks.
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

    // A page that gets no picture in time says the file can't be played.
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
        var timeout = NoPictureWatch.Timeout;
        NoPictureWatch.Timeout = TimeSpan.FromMilliseconds(300);
        try
        {
            window.Show();
            var view = (LibraryView)model.Library.View;
            Sta.PumpUntil(() => view.IsLoaded, "the Library to load");
            model.Library.Click(model.Library.Rows.Single(), ctrl: false, shift: false);
            model.Library.OpenFocused();

            // While the front buffer is gone (a lock screen) the timer waits; it restarts when it's back.
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
            NoPictureWatch.Timeout = timeout;
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

    // The gear's menu is its own popup window: render the menu itself.
    private static void SaveMenu(ContextMenu menu, string name)
    {
        Assert.True(menu.IsOpen);
        var size = new Size(Math.Max(1, menu.ActualWidth), Math.Max(1, menu.ActualHeight));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
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
