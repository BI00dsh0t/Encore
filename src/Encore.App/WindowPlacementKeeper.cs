using System.Windows;
using Encore.Platform.Desktop;

namespace Encore.App;

/// <summary>Keeps the Main App window's size and place across restarts: restored before it shows, saved as it closes.</summary>
internal sealed class WindowPlacementKeeper(string path, Func<IReadOnlyList<Rect>> workAreas, Func<Rect> primaryWorkArea)
{
    // The saved place counts only if this much of the window's top edge, where its title bar is, lands on a screen.
    private const double TitleBarHeight = 32;
    private const double MinimumVisibleWidth = 64;

    /// <summary>The keeper for <paramref name="path"/>, against the monitors' work areas now.</summary>
    public static WindowPlacementKeeper ForFile(string path) => new(path, ScreenWorkAreas, () => SystemParameters.WorkArea);

    /// <summary>The default size, shrunk to the primary screen's work area when that is smaller, but never below the minimum.</summary>
    public static Size DefaultSize(Size wanted, Rect primaryWorkArea, Size minimum) =>
        new(Math.Max(minimum.Width, Math.Min(wanted.Width, primaryWorkArea.Width)),
            Math.Max(minimum.Height, Math.Min(wanted.Height, primaryWorkArea.Height)));

    /// <summary>The saved placement when it is at least the minimum size and its title bar is on a screen; null for the default.</summary>
    public static WindowPlacement? Fit(WindowPlacement? saved, IReadOnlyList<Rect> workAreas, Size minimum)
    {
        if (saved is null || saved.Width < minimum.Width || saved.Height < minimum.Height)
        {
            return null;
        }

        var titleBar = new Rect(saved.Left, saved.Top, saved.Width, TitleBarHeight);
        var onScreen = workAreas.Any(area => Rect.Intersect(area, titleBar) is { IsEmpty: false, Width: >= MinimumVisibleWidth });
        return onScreen ? saved : null;
    }

    /// <summary>On SourceInitialized: the saved bounds and maximized state, never minimized; else the centred default.</summary>
    public void Restore(Window window)
    {
        var minimum = new Size(window.MinWidth, window.MinHeight);
        var fit = Fit(WindowPlacement.Read(path), workAreas(), minimum);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (fit is null)
        {
            var area = primaryWorkArea();
            var size = DefaultSize(new Size(window.Width, window.Height), area, minimum);
            window.Width = size.Width;
            window.Height = size.Height;
            window.Left = area.Left + (area.Width - size.Width) / 2;
            window.Top = area.Top + (area.Height - size.Height) / 2;
            return;
        }

        window.Left = fit.Left;
        window.Top = fit.Top;
        window.Width = fit.Width;
        window.Height = fit.Height;
        if (fit.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>On Closing: the normal bounds, the restore bounds while maximized or minimized; a window never shown saves none.</summary>
    public void Save(Window window)
    {
        var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        if (window.WindowState != WindowState.Normal)
        {
            bounds = window.RestoreBounds;
        }

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized).Write(path);
    }

    // Each monitor's work area in the device-independent pixels a window on it uses, at that monitor's DPI.
    private static IReadOnlyList<Rect> ScreenWorkAreas() =>
        WorkAreas.Current()
            .Select(area =>
            {
                var scale = area.Dpi / 96.0;
                return new Rect(area.Left / scale, area.Top / scale, (area.Right - area.Left) / scale, (area.Bottom - area.Top) / scale);
            })
            .ToList();
}
