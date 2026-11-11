using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Theme;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>A bug that bit: Gaming dark's TextBox template applied a box's Padding twice, so text sat twice as far inset.</summary>
[Collection(nameof(WpfCollection))]
public sealed class GamingTextBoxPaddingTests
{
    [Fact]
    public void AGamingTextBoxsTextSitsAtItsPaddingOnce() => Sta.Run(() =>
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
        var settings = new SettingsWindow
        {
            DataContext = model.Settings,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        ThemeService.ForWindow(window).Apply(AppTheme.GamingDark);
        ThemeService.ForWindow(settings).Apply(AppTheme.GamingDark);
        try
        {
            window.Show();
            model.Library.List.SearchText = "clutch";
            Sta.Pump(TimeSpan.FromMilliseconds(150));
            var offsets = Insets(window);
            Save(window, "gaming-textbox-library.png");

            model.Settings.Select(model.Rewind);
            settings.Show();
            Sta.Pump(TimeSpan.FromMilliseconds(150));
            offsets.AddRange(Insets(settings));
            Save(settings, "gaming-textbox-clips.png");

            // The Library's search box and the Clips page's cache-folder path box, at least.
            Assert.True(offsets.Count >= 2, $"Found {offsets.Count} text boxes");
            Assert.All(offsets, o => Assert.Equal(o.Padding, o.Inset, 0.5));
        }
        finally
        {
            settings.CloseForQuit();
            window.CloseForQuit();
        }
    });

    // Each shown TextBox's padding and where its content presenter actually starts, from the box's left edge.
    private static List<(double Padding, double Inset)> Insets(Window window) =>
        [.. Descendants<TextBox>(window).Where(b => b.IsVisible).Select(b => (b.Padding.Left, Descendants<ScrollContentPresenter>(b).Single().TranslatePoint(default, b).X))];

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
