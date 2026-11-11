using System.IO;
using System.Windows;

namespace Encore.App.Tests;

/// <summary>The Main App window's size and place across restarts: the clamp to the screens, and the trip through window.json.</summary>
[Collection(nameof(WpfCollection))]
public sealed class WindowPlacementTests : IDisposable
{
    private static readonly Size Minimum = new(480, 320);
    private static readonly Rect[] OneScreen = [new Rect(0, 0, 1920, 1040)];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "encore-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void SavedBoundsOnAScreenAreKeptAndOffEveryScreenOrTooSmallGoToTheDefault()
    {
        var onScreen = new WindowPlacement(100, 80, 1320, 700, IsMaximized: true);
        Assert.Same(onScreen, WindowPlacementKeeper.Fit(onScreen, OneScreen, Minimum));
        Assert.Null(WindowPlacementKeeper.Fit(onScreen with { Left = 2500 }, OneScreen, Minimum));
        Assert.Null(WindowPlacementKeeper.Fit(onScreen with { Width = 300 }, OneScreen, Minimum));

        // The default shrinks to a small screen, such as 1920 by 1080 at 150 %.
        Assert.Equal(new Size(1280, 680), WindowPlacementKeeper.DefaultSize(new Size(1320, 700), new Rect(0, 0, 1280, 680), Minimum));
    }

    [Fact]
    public void TheWindowsSizeAndPlaceRoundTripThroughItsFile() => Sta.Run(() =>
    {
        var path = Path.Combine(_folder, "window.json");
        var screen = new Rect(-30000, -30000, 4000, 4000);
        var keeper = new WindowPlacementKeeper(path, () => [screen], () => screen);
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var first = new MainWindow { DataContext = model, Placement = keeper, ShowActivated = false, ShowInTaskbar = false };
        first.Show();
        first.Left = -29000;
        first.Top = -29500;
        first.Width = 1100;
        first.Height = 650;
        first.UpdateLayout();
        first.CloseForQuit();

        var second = new MainWindow { DataContext = model, Placement = keeper, ShowActivated = false, ShowInTaskbar = false };
        second.Show();
        try
        {
            Assert.Equal((-29000.0, -29500.0, 1100.0, 650.0), (second.Left, second.Top, second.ActualWidth, second.ActualHeight));
            Assert.Equal(WindowState.Normal, second.WindowState);
        }
        finally
        {
            second.CloseForQuit();
        }
    });
}
