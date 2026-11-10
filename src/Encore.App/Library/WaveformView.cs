using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Encore.App.Library;

/// <summary>The clip timeline: peak lanes and a video strip on one source-time ruler, with the trim range and playhead.</summary>
internal sealed partial class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveBrushProperty = RegisterBrush(nameof(WaveBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty SelectionBrushProperty = RegisterBrush(nameof(SelectionBrush), Brushes.LightBlue);
    public static readonly DependencyProperty RulerBrushProperty = RegisterBrush(nameof(RulerBrush), Brushes.Gray);
    public static readonly DependencyProperty SurfaceBrushProperty = RegisterBrush(nameof(SurfaceBrush), Brushes.Transparent);
    public static readonly DependencyProperty PlayheadBrushProperty = RegisterBrush(nameof(PlayheadBrush), Brushes.Gray);
    public static readonly DependencyProperty GripBrushProperty = RegisterBrush(nameof(GripBrush), Brushes.Gray);
    public static readonly DependencyProperty DividerBrushProperty = RegisterBrush(nameof(DividerBrush), Brushes.Gray);
    public static readonly DependencyProperty VeilBrushProperty = RegisterBrush(nameof(VeilBrush), Brushes.Gray);

    public static readonly DependencyProperty LaneOffsetProperty = DependencyProperty.Register(
        nameof(LaneOffset),
        typeof(double),
        typeof(WaveformView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceLaneOffset));

    public static readonly DependencyProperty ScrollMaximumProperty = DependencyProperty.Register(
        nameof(ScrollMaximum), typeof(double), typeof(WaveformView), new PropertyMetadata(0d));

    private const double LabelWidth = 100;
    private const double LaneHeight = 40;
    private const double FilmstripHeight = 48;

    // The ruler's band at the top; clicks on it always seek.
    private const double RulerHeight = 28;

    // How many audio lanes show at once; more scroll.
    private const int VisibleLanes = 5;

    private ClipCreatorPage? _page;
    private DrawingGroup? _waveformDrawing;

    public WaveformView()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 52;
        DataContextChanged += (_, _) => Attach(DataContext as ClipCreatorPage);
        Loaded += (_, _) => Attach(DataContext as ClipCreatorPage);
        Unloaded += (_, _) =>
        {
            ReleaseMouseCapture();
            Attach(null);
        };
    }

    /// <summary>The first audio lane shown; whole numbers from 0 to <see cref="ScrollMaximum"/>.</summary>
    public double LaneOffset
    {
        get => (double)GetValue(LaneOffsetProperty);
        set => SetValue(LaneOffsetProperty, value);
    }

    /// <summary>How many lanes the list can scroll by; 0 when every lane shows.</summary>
    public double ScrollMaximum
    {
        get => (double)GetValue(ScrollMaximumProperty);
        private set => SetValue(ScrollMaximumProperty, value);
    }

    public Brush WaveBrush
    {
        get => (Brush)GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public Brush SelectionBrush
    {
        get => (Brush)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public Brush RulerBrush
    {
        get => (Brush)GetValue(RulerBrushProperty);
        set => SetValue(RulerBrushProperty, value);
    }

    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public Brush PlayheadBrush
    {
        get => (Brush)GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public Brush GripBrush
    {
        get => (Brush)GetValue(GripBrushProperty);
        set => SetValue(GripBrushProperty, value);
    }

    public Brush DividerBrush
    {
        get => (Brush)GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    public Brush VeilBrush
    {
        get => (Brush)GetValue(VeilBrushProperty);
        set => SetValue(VeilBrushProperty, value);
    }

    // The audio lanes end where the video strip starts.
    private double AudioBottom => Math.Max(RulerHeight, ActualHeight - FilmstripHeight);

    // The plot starts right of the track labels, or at the right edge when the control is narrower.
    private double PlotLeft => Math.Min(LabelWidth, Math.Max(0, ActualWidth - 1));

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == WaveBrushProperty
            || e.Property == RulerBrushProperty
            || e.Property == LaneOffsetProperty
            || e.Property == DividerBrushProperty)
        {
            _waveformDrawing = null;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        FinishPointerForViewportChange();
        _waveformDrawing = null;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _waveformDrawing = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Math.Max(_page?.Waveforms.Length ?? 0, _page?.Tracks.Count(track => track.Included) ?? 0);
        ScrollMaximum = Math.Max(0, lanes - VisibleLanes);
        CoerceValue(LaneOffsetProperty);
        var width = double.IsPositiveInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, RulerHeight + Math.Max(1, Math.Min(VisibleLanes, lanes)) * LaneHeight + FilmstripHeight);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private static DependencyProperty RegisterBrush(string name, Brush fallback) => DependencyProperty.Register(
        name, typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static object CoerceLaneOffset(DependencyObject view, object value) =>
        Math.Clamp(Math.Round((double)value), 0, ((WaveformView)view).ScrollMaximum);

    // What changes the cached lanes and video strip; anything else only moves the overlays.
    private static bool ChangesWaveformDrawing(string? property) => property is nameof(ClipCreatorPage.Waveforms)
        or nameof(ClipCreatorPage.Metadata)
        or nameof(ClipCreatorPage.ViewportStart)
        or nameof(ClipCreatorPage.ViewportEnd)
        or nameof(ClipCreatorPage.Thumbnails);

    private void Attach(ClipCreatorPage? page)
    {
        ReleaseMouseCapture();
        _page?.EndScrub();
        _page?.EndRangeDrag();
        _pointerMode = PointerMode.None;
        _suppressDoubleClick = false;
        Cursor = null;
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }

        _page = page;
        _waveformDrawing = null;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipCreatorPage.ViewportStart) or nameof(ClipCreatorPage.ViewportEnd))
        {
            FinishPointerForViewportChange();
        }

        if (e.PropertyName == nameof(ClipCreatorPage.CanEdit))
        {
            if (_page is not { CanEdit: true })
            {
                FinishPointerForViewportChange();
            }

            UpdatePointerCursor(_lastPoint);
        }

        if (e.PropertyName is nameof(ClipCreatorPage.Waveforms) or nameof(ClipCreatorPage.Metadata))
        {
            InvalidateMeasure();
        }

        if (ChangesWaveformDrawing(e.PropertyName))
        {
            _waveformDrawing = null;
        }

        InvalidateVisual();
    }

    private TimelineScale ScaleOf(ClipCreatorPage page) =>
        new(PlotLeft, ActualWidth, page.ViewportStart, page.ViewportEnd);

    private double XOf(double time) => _page is { } page ? ScaleOf(page).XOf(time) : 0;

    private double TimeAt(double x) => _page is { } page ? ScaleOf(page).TimeAt(x) : 0;
}
