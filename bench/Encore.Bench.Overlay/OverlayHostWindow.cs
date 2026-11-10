using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Encore.Platform.Desktop;

namespace Encore.Bench.Overlay;

/// <summary>A stand-in for the App's Overlay window showing the hollow dot, built the same way; Bench never references the App.</summary>
internal sealed class OverlayHostWindow : Window
{
    private const double DotSizeDip = 16;
    private const double InsetDip = 24;

    private readonly int? _gameProcessId;

    public OverlayHostWindow(int? gameProcessId)
    {
        _gameProcessId = gameProcessId;
        var red = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        red.Freeze();
        Title = "Rewind Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = DotSizeDip;
        Height = DotSizeDip;
        var dot = new Ellipse { IsHitTestVisible = false, Stroke = red, StrokeThickness = 2 };
        Content = new Viewbox { Child = new Grid { Width = DotSizeDip, Height = DotSizeDip, Children = { dot } }, IsHitTestVisible = false };
    }

    public nint Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Places the dot top-right on the game's monitor and shows it, as the App's Overlay does.</summary>
    public void ShowDot()
    {
        var (x, y, size) = Monitors.TopRight(Monitors.ForProcess(_gameProcessId), DotSizeDip, InsetDip);
        OverlayWindowStyle.ShowWithoutActivation(Handle, x, y, size, size);
        if (!IsVisible)
        {
            Show();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // ShellWindow's exclusion, then the Overlay's styles, in the App's order.
        CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);
        base.OnSourceInitialized(e);
        OverlayWindowStyle.Apply(new WindowInteropHelper(this).Handle);
    }
}
