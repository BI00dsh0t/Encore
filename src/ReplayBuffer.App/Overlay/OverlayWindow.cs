using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Overlay;

/// <summary>
/// The Overlay: a dot in the top-right corner of the Target Game's monitor, in a topmost,
/// click-through window that is never activated and is excluded from capture (AD-15). It is hidden
/// while there is no dot. Nothing is drawn in or hooked onto the game (NFR-3).
/// </summary>
internal sealed class OverlayWindow : ShellWindow
{
    /// <summary>The dot's diameter.</summary>
    public const double DotSizeDip = 16;

    /// <summary>How far the dot sits in from the monitor's top and right edges.</summary>
    public const double InsetDip = 24;

    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));

    private readonly OverlayViewModel _model;
    private readonly Ellipse _dot = new() { IsHitTestVisible = false };

    public OverlayWindow(OverlayViewModel model)
    {
        _model = model;
        Title = "Replay Buffer Overlay";
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
        Content = new Viewbox { Child = new Grid { Width = DotSizeDip, Height = DotSizeDip, Children = { _dot } }, IsHitTestVisible = false };

        model.PropertyChanged += (_, _) => Update();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Update();
    }

    /// <summary>The window's handle, created if it doesn't exist yet.</summary>
    public nint Handle => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayWindowStyle.Apply(new WindowInteropHelper(this).Handle);
    }

    private void Update()
    {
        switch (_model.Dot)
        {
            case OverlayDot.None:
                Hide();
                return;
            case OverlayDot.Hollow:
                _dot.Fill = null;
                _dot.Stroke = Red;
                _dot.StrokeThickness = 2;
                break;
            default:
                _dot.Fill = Red;
                _dot.Stroke = null;
                break;
        }

        Place();

        // WPF's own state follows the window; ShowActivated is false, so this shows with SW_SHOWNA.
        if (!IsVisible)
        {
            Show();
        }
    }

    // In physical pixels, so WPF's DIP conversion across mixed-DPI monitors doesn't matter.
    private void Place()
    {
        if (_model.Dot == OverlayDot.None)
        {
            return;
        }

        var (x, y, size) = Monitors.TopRight(Monitors.ForProcess(_model.TargetProcessId), DotSizeDip, InsetDip);
        OverlayWindowStyle.ShowWithoutActivation(Handle, x, y, size, size);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
