using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Theme;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The General page's Games section and its Capture new games switch.</summary>
[Collection(nameof(WpfCollection))]
public sealed class CaptureNewGamesTests
{
    [Fact]
    public void TheSwitchStartsOnAndTurningItOffSendsTheGlobalKey()
    {
        var engine = new FakeEngineSession { Reply = (_, _) => new SettingsUpdateResult(1) };
        var model = new MainViewModel(engine, action => action());
        Assert.True(model.General.CaptureNewGames);

        model.General.CaptureNewGames = false;

        var op = Assert.Single(Assert.IsType<SettingsUpdate>(Assert.Single(engine.Sent)).Ops);
        Assert.Equal((SettingsScope.Global, "captureNewGames", SettingsOpKind.Set, false), (op.Scope, op.Key, op.Op, op.Value.GetBoolean()));
    }

    [Fact]
    public void TheGeneralPageShowsTheSwitchInBothThemes() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
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
            model.SelectedSettingsViewModel = model.General;
            foreach (var (theme, file) in new[] { (AppTheme.Fluent, "general-games-fluent.png"), (AppTheme.GamingDark, "general-games-gaming.png") })
            {
                ThemeService.ForWindow(window).Apply(theme);
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                var box = Descendants<CheckBox>(window).Single(b => Equals(b.Content, "Capture new games"));
                Assert.True(box.IsVisible);
                Assert.True(box.IsChecked);
                Descendants<ScrollViewer>(window).First(v => v.IsVisible && v.ScrollableHeight > 0).ScrollToEnd();
                Sta.Pump(TimeSpan.FromMilliseconds(150));
                Save(window, file);
            }
        }
        finally
        {
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
