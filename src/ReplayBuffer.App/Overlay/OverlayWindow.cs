using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Overlay;

/// <summary>
/// The Overlay: a dot in the top-right corner of the Target Game's monitor, with the green saving
/// icon to its left while a Clip is being saved (R2), and the queued message below them (R3), green
/// for a notice and red for an error, in a topmost, click-through window that is never activated and
/// is excluded from capture (AD-15). The window is one fixed-width strip, so the dot stays put as the
/// icon comes and goes; while a message shows it grows left and down, the dot still in its place. It
/// is hidden while there is none of the three. Nothing is drawn in or hooked onto the game (NFR-3).
/// </summary>
internal sealed class OverlayWindow : ShellWindow
{
    /// <summary>The dot's diameter.</summary>
    public const double DotSizeDip = 16;

    /// <summary>How far the dot sits in from the monitor's top and right edges.</summary>
    public const double InsetDip = 24;

    /// <summary>The saving icon's width and height.</summary>
    public const double IconSizeDip = 16;

    /// <summary>The space between the saving icon and the dot.</summary>
    public const double GapDip = 6;

    /// <summary>The message's width at most, right-aligned to the dot.</summary>
    public const double MessageWidthDip = 260;

    /// <summary>The message's height.</summary>
    public const double MessageHeightDip = 22;

    /// <summary>The space between the strip and the message.</summary>
    public const double MessageGapDip = 6;

    private const double WidthDip = IconSizeDip + GapDip + DotSizeDip;

    private const double MessageAreaHeightDip = DotSizeDip + MessageGapDip + MessageHeightDip;

    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)));
    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)));

    // A downward arrow over a bar, in the icon's 16 DIP square.
    private static readonly Geometry SaveGlyph = Freeze(Geometry.Parse("M6,1 H10 V7 H13 L8,12 L3,7 H6 Z M2,13 H14 V15.5 H2 Z"));

    private readonly OverlayViewModel _model;
    private readonly Ellipse _dot = new() { Width = DotSizeDip, Height = DotSizeDip, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _icon = new() { Data = SaveGlyph, Fill = Green, Width = IconSizeDip, Height = IconSizeDip, IsHitTestVisible = false };
    private readonly TextBlock _text = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Border _message;
    private readonly Grid _root;

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
        Width = WidthDip;
        Height = DotSizeDip;
        _icon.Margin = new Thickness(0, 0, GapDip, 0);
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = WidthDip,
            Height = DotSizeDip,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _icon, _dot },
        };
        _message = new Border
        {
            Background = Shade,
            Padding = new Thickness(6, 2, 6, 2),
            Height = MessageHeightDip,
            MaxWidth = MessageWidthDip,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _text,
        };
        _root = new Grid { Width = WidthDip, Height = DotSizeDip, IsHitTestVisible = false, Children = { strip, _message } };

        // Top-right, so the dot keeps its corner if rounding leaves the window a pixel off the grid's shape.
        Content = new Viewbox { Child = _root, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };

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
        if (!Showing)
        {
            Hide();
            return;
        }

        // Hidden, not collapsed: the dot keeps its place in the strip.
        _icon.Visibility = _model.Saving ? Visibility.Visible : Visibility.Hidden;
        _dot.Visibility = _model.Dot == OverlayDot.None ? Visibility.Hidden : Visibility.Visible;
        switch (_model.Dot)
        {
            case OverlayDot.None:
                break;
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

        // The window is the strip alone unless a message shows below it.
        if (_model.Message is { } message)
        {
            _text.Text = message.Message;
            _text.Foreground = message.Severity == WarningSeverity.Error ? Red : Green;
            _message.Visibility = Visibility.Visible;
            _root.Width = MessageWidthDip;
            _root.Height = MessageAreaHeightDip;
        }
        else
        {
            _message.Visibility = Visibility.Collapsed;
            _root.Width = WidthDip;
            _root.Height = DotSizeDip;
        }

        Place();

        // WPF's own state follows the window; ShowActivated is false, so this shows with SW_SHOWNA.
        if (!IsVisible)
        {
            Show();
        }
    }

    private bool Showing => _model.Dot != OverlayDot.None || _model.Saving || _model.Message is not null;

    // In physical pixels, so WPF's DIP conversion across mixed-DPI monitors doesn't matter. The dot
    // sits where Monitors.TopRight puts it; the icon and the gap extend the window to its left, and
    // a message extends it further left and down.
    private void Place()
    {
        if (!Showing)
        {
            return;
        }

        var (x, y, size) = Monitors.TopRight(Monitors.ForProcess(_model.TargetProcessId), DotSizeDip, InsetDip);
        var scale = size / DotSizeDip;
        var (width, height) = _model.Message is null ? (WidthDip, DotSizeDip) : (MessageWidthDip, MessageAreaHeightDip);
        var left = (int)Math.Round((width - DotSizeDip) * scale);
        var bottom = (int)Math.Round((height - DotSizeDip) * scale);
        OverlayWindowStyle.ShowWithoutActivation(Handle, x - left, y, left + size, size + bottom);
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
