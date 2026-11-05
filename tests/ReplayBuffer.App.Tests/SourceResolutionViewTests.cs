using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Settings;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>Story 12.3: the Clips page's and a game's Video quality with Source chosen, in both themes.</summary>
[Collection(nameof(WpfCollection))]
public sealed class SourceResolutionViewTests
{
    [Fact]
    public void TheClipsPageShowsSourceAndItsHelpInBothThemes() => Sta.Run(() =>
    {
        var model = new MainViewModel(SourceEngine(), action => action());
        var window = new MainWindow
        {
            DataContext = model,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            model.IsSettingsTabSelected = true;
            model.SelectedPage = model.ReplayBuffer;
            foreach (var (theme, file) in new[] { (AppTheme.Fluent, "clips-source-fluent.png"), (AppTheme.GamingDark, "clips-source-gaming.png") })
            {
                ThemeService.ForWindow(window).Apply(theme);
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                var resolution = Descendants<ComboBox>(window).Single(c => c.SelectedItem is ResolutionItem);
                Assert.Equal(ReplayBufferSettingsPage.SourceLabel, ((ResolutionItem)resolution.SelectedItem).Label);
                var help = Descendants<TextBlock>(window).Single(t => t.Text == model.ReplayBuffer.SourceHelp);
                Descendants<ScrollViewer>(window).First(v => v.IsVisible && v.ScrollableHeight > 0).ScrollToEnd();
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                Assert.True(help.IsVisible);
                Save(window, file);
            }
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    [Fact]
    public void TheGamesVideoEditorShowsSourceAndItsHelpInBothThemes() => Sta.Run(() =>
    {
        // A game's own Video quality (GameVideoEditor) binds the same view model as the Clips page.
        var model = new MainViewModel(SourceEngine(), action => action());
        var window = new Window
        {
            Content = new Border { Padding = new Thickness(16), Child = new GameVideoEditor { DataContext = model.ReplayBuffer } },
            Width = 720,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            foreach (var (theme, file) in new[] { (AppTheme.Fluent, "game-video-source-fluent.png"), (AppTheme.GamingDark, "game-video-source-gaming.png") })
            {
                ThemeService.ForWindow(window).Apply(theme);
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                var resolution = Descendants<ComboBox>(window).Single(c => c.SelectedItem is ResolutionItem);
                Assert.Equal(ReplayBufferSettingsPage.SourceLabel, ((ResolutionItem)resolution.SelectedItem).Label);
                Assert.True(Descendants<TextBlock>(window).Single(t => t.Text == model.ReplayBuffer.SourceHelp).IsVisible);
                Save(window, file);
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static FakeEngineSession SourceEngine()
    {
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            Settings = SettingsDocument.Empty with
            {
                Revision = 1,
                Global = SettingsMaps.EmptyValues.Add(Keys.Video.Name, Keys.Video.ToJson(Keys.Video.Default with { MatchSource = true })),
            },
        };
        return engine;
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
