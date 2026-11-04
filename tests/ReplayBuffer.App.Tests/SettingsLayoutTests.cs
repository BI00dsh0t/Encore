using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Settings;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>The shared settings layout (story 9.5): sections as cards in both themes, and the page error info bar.</summary>
[Collection(nameof(WpfCollection))]
public sealed partial class SettingsLayoutTests
{
    private static readonly string[] Views =
        ["GeneralSettingsView", "ReplayBufferSettingsView", "RecordingsSettingsView", "HotkeysView", "FeedbackSoundsView"];

    [Fact]
    public void TheFivePagesShowTheirSectionsAsCardsInBothThemes() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var window = Offscreen(new MainWindow { DataContext = model });
        var theme = ThemeService.ForWindow(window);
        var expected = new (SettingsPage Page, string Slug, string[] Sections)[]
        {
            (model.General, "general", ["Appearance", "Startup and Overlay"]),
            (model.ReplayBuffer, "replay-buffer", ["Replay Buffer", "Output"]),
            (model.Recordings, "recordings", ["Recordings"]),
            (model.Hotkeys, "hotkeys", ["Hotkeys"]),
            (model.FeedbackSounds, "feedback-sounds", ["Feedback Sounds"]),
        };
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(which);
                window.Show();
                var card = (Style)window.FindResource("RbCardStyle");
                foreach (var (page, slug, sections) in expected)
                {
                    model.SelectedNavItem = model.NavItems.First(i => i.Page == page);
                    Sta.Pump(TimeSpan.FromMilliseconds(100));

                    Assert.True(page.View.IsLoaded, $"{page.Title} didn't load under {which}");
                    var found = Descendants<HeaderedContentControl>(page.View).ToList();
                    Assert.Equal(sections, found.Select(s => s.Header as string));
                    Assert.All(found, s => Assert.Contains(Descendants<Border>(s), b => b.Style == card));
                    Save(window, $"settings-{slug}-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");
                }
            }
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    [Fact]
    public void ARefusedUpdateShowsTheDangerBarUntilAnUpdateCommits() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var model = new MainViewModel(engine, action => action());
        var window = Offscreen(new MainWindow { DataContext = model });
        ThemeService.ForWindow(window).Apply(AppTheme.Fluent);
        var message = (ContentControl)window.FindName("PageMessage");
        try
        {
            window.Show();
            model.SelectedNavItem = model.NavItems.First(i => i.Page == model.General);
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(message.IsVisible);

            engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, "Disk full.");
            model.General.StartWithWindows = true;
            Sta.Pump(TimeSpan.FromMilliseconds(50));

            Assert.True(message.IsVisible);
            Assert.Equal("Not saved (settings-write-failed): Disk full.", message.Content);
            var danger = ((SolidColorBrush)window.FindResource("SystemFillColorCriticalBrush")).Color;
            Assert.Contains(Descendants<Border>(message), b => b.BorderBrush is SolidColorBrush brush && brush.Color == danger);

            engine.Reply = (_, _) => new SettingsUpdateResult(1);
            model.General.StartWithWindows = true;
            Sta.Pump(TimeSpan.FromMilliseconds(50));
            Assert.False(message.IsVisible);
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    [Fact]
    public void TheFiveViewsHaveNoLiteralColourAndNoKeylessStyle()
    {
        foreach (var view in Views)
        {
            var xaml = File.ReadAllText(Path.Combine(MainFolder(), "src", "ReplayBuffer.App", "Settings", view + ".xaml"));
            Assert.False(LiteralColour().IsMatch(xaml), $"{view} has a literal colour");
            Assert.False(KeylessStyle().IsMatch(xaml), $"{view} has a key-less style");
        }
    }

    private static MainWindow Offscreen(MainWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    // The builder's hand check against the renders, beside the test binary.
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

    private static string MainFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReplayBuffer.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ReplayBuffer.slnx wasn't found above the test.");
    }

    [GeneratedRegex(@"""(Gray|DarkRed)""|#[0-9A-Fa-f]{3,8}\b")]
    private static partial Regex LiteralColour();

    [GeneratedRegex(@"<Style(?![^>]*x:Key)[^>]*TargetType=")]
    private static partial Regex KeylessStyle();
}
