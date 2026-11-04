using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Overlay;

/// <summary>
/// The Overlay: a dot in the top-right corner of the Target Game's monitor; to its left, right to
/// left, the mic-muted icon while the mic is muted, the green saving icon while a Clip is being saved
/// (R2) and a red X while the disk is full (R3); and the queued message below them (R3) on the
/// theme's card, in its success colour for a notice and its danger colour for an error. The glyphs keep
/// fixed colours in both themes (AD-23). It is a topmost, click-through window that is never activated
/// and is excluded from capture (AD-15). The window is one fixed-width strip of fixed slots, so the dot stays
/// put as the icons come and go; while a message shows it grows left and down, the dot still in its
/// place. It is hidden while there is no dot, icon, X or message. Nothing is drawn in or hooked onto
/// the game (NFR-3).
/// </summary>
internal sealed class OverlayWindow : ShellWindow
{
    /// <summary>The dot's diameter.</summary>
    public const double DotSizeDip = 16;

    /// <summary>How far the dot sits in from the monitor's top and right edges.</summary>
    public const double InsetDip = 24;

    /// <summary>Each icon's width and height: the mic, the saving icon and the X.</summary>
    private const double IconSizeDip = 16;

    /// <summary>The space after each icon, toward the dot.</summary>
    private const double GapDip = 6;

    /// <summary>The message's width at most, right-aligned to the dot.</summary>
    private const double MessageWidthDip = 260;

    /// <summary>The message's height.</summary>
    private const double MessageHeightDip = 22;

    /// <summary>The space between the strip and the message.</summary>
    private const double MessageGapDip = 6;

    private const double WidthDip = (3 * (IconSizeDip + GapDip)) + DotSizeDip;

    private const double MessageAreaHeightDip = DotSizeDip + MessageGapDip + MessageHeightDip;

    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)));
    private static readonly Brush Light = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)));

    // The message card's theme keys (DESIGN.md "Overlay message card"); the glyphs above keep fixed colours.
    internal const string CardBrushKey = "RbOverlayCardBrush";
    internal const string CardStrokeKey = "SurfaceStrokeColorFlyoutBrush";
    internal const string ErrorTextKey = "SystemFillColorCriticalBrush";
    internal const string NoticeTextKey = "SystemFillColorSuccessBrush";

    // A downward arrow over a bar, in the icon's 16 DIP square.
    private static readonly Geometry SaveGlyph = Freeze(Geometry.Parse("M6,1 H10 V7 H13 L8,12 L3,7 H6 Z M2,13 H14 V15.5 H2 Z"));

    // A microphone capsule on a stand, in the icon's 16 DIP square.
    private static readonly Geometry MicGlyph = Freeze(Geometry.Parse(
        "F1 M8,1 A2.5,2.5 0 0 1 10.5,3.5 V6.5 A2.5,2.5 0 0 1 5.5,6.5 V3.5 A2.5,2.5 0 0 1 8,1 Z "
        + "M3.5,7 H5 A3,3 0 0 0 11,7 H12.5 A4.5,4.5 0 0 1 8.75,11.45 V13.5 H11 V15 H5 V13.5 H7.25 V11.45 A4.5,4.5 0 0 1 3.5,7 Z"));

    private static readonly Geometry SlashGlyph = Freeze(Geometry.Parse("M2,2 L14,14"));

    private static readonly Geometry CrossGlyph = Freeze(Geometry.Parse("M3,3 L13,13 M13,3 L3,13"));

    private readonly OverlayViewModel _model;
    private readonly Func<int?, MonitorArea> _locate;
    private readonly Ellipse _dot = new() { Width = DotSizeDip, Height = DotSizeDip, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _icon = new() { Data = SaveGlyph, Fill = Green, Width = IconSizeDip, Height = IconSizeDip, IsHitTestVisible = false };
    private readonly Grid _mic = new() { Width = IconSizeDip, Height = IconSizeDip, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _cross = new()
    {
        Data = CrossGlyph,
        Stroke = Red,
        StrokeThickness = 2.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = IconSizeDip,
        Height = IconSizeDip,
        IsHitTestVisible = false,
    };

    private readonly TextBlock _text = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Border _message;
    private readonly Grid _root;

    /// <param name="model">What the Overlay shows.</param>
    /// <param name="locate">
    /// The monitor of a process (null: the primary), which the dot sits on (5.1's rule); <c>Monitors.ForProcess</c>
    /// when null. Read again at each placement, so a display change moves the window (T-03).
    /// </param>
    public OverlayWindow(OverlayViewModel model, Func<int?, MonitorArea>? locate = null)
    {
        _model = model;
        _locate = locate ?? Monitors.ForProcess;
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
        _mic.Margin = new Thickness(0, 0, GapDip, 0);
        _cross.Margin = new Thickness(0, 0, GapDip, 0);
        _mic.Children.Add(new System.Windows.Shapes.Path { Data = MicGlyph, Fill = Light, IsHitTestVisible = false });
        _mic.Children.Add(new System.Windows.Shapes.Path
        {
            Data = SlashGlyph,
            Stroke = Red,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        });
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = WidthDip,
            Height = DotSizeDip,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _cross, _icon, _mic, _dot },
        };
        // The card is the Overlay's one themed element: its fill, stroke, corner and type follow the theme
        // live through resource references, while the window itself stays transparent.
        _text.SetResourceReference(StyleProperty, "CaptionTextBlockStyle");
        _message = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 2, 7, 2),
            Height = MessageHeightDip,
            MaxWidth = MessageWidthDip,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _text,
        };
        _message.SetResourceReference(Border.BackgroundProperty, CardBrushKey);
        _message.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
        _message.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
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

        // Hidden, not collapsed: every slot, and so the dot, keeps its place in the strip.
        _cross.Visibility = _model.DiskFull ? Visibility.Visible : Visibility.Hidden;
        _mic.Visibility = _model.MicMuted ? Visibility.Visible : Visibility.Hidden;
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
            _text.SetResourceReference(TextBlock.ForegroundProperty, message.Severity == WarningSeverity.Error ? ErrorTextKey : NoticeTextKey);
            _message.Visibility = Visibility.Visible;
        }
        else
        {
            _message.Visibility = Visibility.Collapsed;
        }

        (_root.Width, _root.Height) = ExtentDip;
        Place();

        // WPF's own state follows the window; ShowActivated is false, so this shows with SW_SHOWNA.
        if (!IsVisible)
        {
            Show();
        }
    }

    private bool Showing => _model.Dot != OverlayDot.None || _model.Saving || _model.Message is not null || _model.DiskFull;

    // The window's size: the strip alone, or the message area while a message shows.
    private (double Width, double Height) ExtentDip =>
        _model.Message is null ? (WidthDip, DotSizeDip) : (MessageWidthDip, MessageAreaHeightDip);

    // In physical pixels, so WPF's DIP conversion across mixed-DPI monitors doesn't matter. The dot
    // sits where Monitors.TopRight puts it; the icons and the gaps extend the window to its left, and
    // a message extends it further left and down.
    private void Place()
    {
        if (!Showing)
        {
            return;
        }

        var (x, y, size) = Monitors.TopRight(_locate(_model.TargetProcessId), DotSizeDip, InsetDip);
        var scale = size / DotSizeDip;
        var (width, height) = ExtentDip;
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
