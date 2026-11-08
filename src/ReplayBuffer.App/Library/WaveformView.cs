using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace ReplayBuffer.App.Library;

/// <summary>Bounded peak lanes on one source-time ruler. Keys edit the same values as pointer handles.</summary>
internal sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(nameof(WaveBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(nameof(SelectionBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.LightBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RulerBrushProperty = DependencyProperty.Register(nameof(RulerBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(nameof(SurfaceBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayheadBrushProperty = DependencyProperty.Register(nameof(PlayheadBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GripBrushProperty = DependencyProperty.Register(nameof(GripBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(nameof(DividerBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VeilBrushProperty = DependencyProperty.Register(nameof(VeilBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LaneOffsetProperty = DependencyProperty.Register(nameof(LaneOffset), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, (d, value) => Math.Clamp(Math.Round((double)value), 0, ((WaveformView)d).ScrollMaximum)));
    public static readonly DependencyProperty ScrollMaximumProperty = DependencyProperty.Register(nameof(ScrollMaximum), typeof(double), typeof(WaveformView), new PropertyMetadata(0d));
    public double LaneOffset { get => (double)GetValue(LaneOffsetProperty); set => SetValue(LaneOffsetProperty, value); }
    public double ScrollMaximum { get => (double)GetValue(ScrollMaximumProperty); private set => SetValue(ScrollMaximumProperty, value); }
    private const double LabelWidth = 100;
    private const double LaneHeight = 40;
    private const double FilmstripHeight = 48;
    private double AudioBottom => Math.Max(28, ActualHeight - FilmstripHeight);
    private double PlotLeft => Math.Min(LabelWidth, Math.Max(0, ActualWidth - 1));
    private ClipCreatorPage? _page;
    private enum PointerMode { None, In, Out, Seek, PendingMove, Move }
    private PointerMode _pointerMode;
    private Point _pressPoint, _lastPoint;
    private double _moveStart, _moveEnd, _secondsPerPixel;
    private bool _suppressDoubleClick;
    private bool _endHandle;
    private bool _seekFocus = true;
    private DrawingGroup? _waveformDrawing;

    public WaveformView()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 52;
        DataContextChanged += (_, _) => Attach(DataContext as ClipCreatorPage);
        Loaded += (_, _) => Attach(DataContext as ClipCreatorPage);
        Unloaded += (_, _) => { ReleaseMouseCapture(); Attach(null); };
    }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush RulerBrush { get => (Brush)GetValue(RulerBrushProperty); set => SetValue(RulerBrushProperty, value); }
    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    public Brush PlayheadBrush { get => (Brush)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public Brush GripBrush { get => (Brush)GetValue(GripBrushProperty); set => SetValue(GripBrushProperty, value); }
    public Brush DividerBrush { get => (Brush)GetValue(DividerBrushProperty); set => SetValue(DividerBrushProperty, value); }
    public Brush VeilBrush { get => (Brush)GetValue(VeilBrushProperty); set => SetValue(VeilBrushProperty, value); }

    private void Attach(ClipCreatorPage? page)
    {
        ReleaseMouseCapture();
        _page?.EndScrub();
        _page?.EndRangeDrag();
        _pointerMode = PointerMode.None;
        _suppressDoubleClick = false;
        Cursor = null;
        if (_page is not null) { _page.PropertyChanged -= Changed; }
        _page = page;
        _waveformDrawing = null;
        if (_page is not null) { _page.PropertyChanged += Changed; }
        InvalidateMeasure();
        InvalidateVisual();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipCreatorPage.ViewportStart) or nameof(ClipCreatorPage.ViewportEnd)) { FinishPointerForViewportChange(); }
        if (e.PropertyName == nameof(ClipCreatorPage.CanEdit))
        {
            if (_page is not { CanEdit: true }) { FinishPointerForViewportChange(); }
            UpdatePointerCursor(_lastPoint);
        }
        if (e.PropertyName is nameof(ClipCreatorPage.Waveforms) or nameof(ClipCreatorPage.Metadata)) { InvalidateMeasure(); }
        if (e.PropertyName is nameof(ClipCreatorPage.Waveforms) or nameof(ClipCreatorPage.Metadata) or nameof(ClipCreatorPage.ViewportStart) or nameof(ClipCreatorPage.ViewportEnd) or nameof(ClipCreatorPage.Thumbnails)) { _waveformDrawing = null; }
        InvalidateVisual();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == WaveBrushProperty || e.Property == RulerBrushProperty || e.Property == LaneOffsetProperty || e.Property == DividerBrushProperty)
        { _waveformDrawing = null; }
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
        var lanes = Math.Max(_page?.Waveforms.Length ?? 0, _page?.Tracks.Count(t => t.Included) ?? 0);
        ScrollMaximum = Math.Max(0, lanes - 5);
        CoerceValue(LaneOffsetProperty);
        return new Size(double.IsPositiveInfinity(availableSize.Width) ? 0 : availableSize.Width, 28 + Math.Max(1, Math.Min(5, lanes)) * LaneHeight + FilmstripHeight);
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
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(SurfaceBrush, null, new Rect(RenderSize));
        if (_page is not { Metadata: not null } page || page.ViewportEnd <= page.ViewportStart) { return; }
        var plotWidth = Math.Max(1, ActualWidth - PlotLeft);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var role = _seekFocus ? "Playhead" : _endHandle ? "Out trim" : "In trim";
        System.Windows.Automation.AutomationProperties.SetName(this, $"{role}. Audio waveform, video strip and trim selection. " + string.Join(", ", page.Waveforms.Skip((int)LaneOffset).Take(5).Select(lane => page.Tracks.FirstOrDefault(t => t.Track.StreamIndex == lane.StreamIndex)?.Name ?? "Audio")));
        var labels = Math.Max(1, (int)(plotWidth / 140));
        for (var i = 0; i <= labels; i++)
        {
            var x = PlotLeft + plotWidth * i / labels;
            dc.DrawLine(new Pen(DividerBrush, 1), new(x, 20), new(x, AudioBottom));
            var text = new FormattedText(ClipCreatorPage.FormatTime(page.ViewportStart + (page.ViewportEnd - page.ViewportStart) * i / labels),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi);
            if (i > 0 && plotWidth < text.Width * 2 + 8) { continue; }
            var labelX = i == 0 ? x : i == labels ? x - text.Width : x - text.Width / 2;
            dc.DrawText(text, new Point(Math.Max(PlotLeft, labelX), 2));
        }
        if (_waveformDrawing is null) { _waveformDrawing = CreateWaveformDrawing(page, plotWidth, dpi); }
        dc.DrawDrawing(_waveformDrawing);
        var videoLabel = new FormattedText("Video", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi);
        dc.DrawText(videoLabel, new Point(4, AudioBottom + (FilmstripHeight - videoLabel.Height) / 2));
        if (page.Waveforms.Length == 0 && !page.Tracks.Any(t => t.Included))
        {
            var empty = new FormattedText("No audio tracks selected", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi);
            dc.DrawText(empty, new Point(4, 30));
        }
        var left = X(page.Start); var right = X(page.End);
        dc.PushClip(new RectangleGeometry(new Rect(PlotLeft, 28, plotWidth, Math.Max(0, ActualHeight - 28))));
        dc.PushOpacity(0.55);
        dc.DrawRectangle(VeilBrush, null, new Rect(PlotLeft, 28, Math.Clamp(left - PlotLeft, 0, plotWidth), Math.Max(0, ActualHeight - 28)));
        var after = Math.Clamp(right, PlotLeft, ActualWidth);
        dc.DrawRectangle(VeilBrush, null, new Rect(after, 28, ActualWidth - after, Math.Max(0, ActualHeight - 28)));
        dc.Pop();
        dc.PushOpacity(page.CanEdit ? 1 : 0.45);
        var selection = new Rect(left, 28, Math.Max(0, right - left), Math.Max(1, ActualHeight - 31));
        dc.PushOpacity(0.09); dc.DrawRoundedRectangle(SelectionBrush, null, selection, 9, 9); dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(SelectionBrush, 2), selection, 9, 9);
        foreach (var x in new[] { left, right })
        {
            if (x < PlotLeft || x > ActualWidth) { continue; }
            var handle = new Rect(x - 6, 28, 12, Math.Max(1, ActualHeight - 31));
            dc.DrawRoundedRectangle(SelectionBrush, null, handle, 7, 7);
            dc.DrawLine(new Pen(GripBrush, 2), new(x, handle.Top + (handle.Height - 17) / 2), new(x, handle.Top + (handle.Height + 17) / 2));
            if (IsKeyboardFocused && !_seekFocus && (x == left ? !_endHandle : _endHandle))
            { dc.DrawRoundedRectangle(null, new Pen(GripBrush, 2), new Rect(x - 9, 29, 18, Math.Max(1, ActualHeight - 33)), 7, 7); }
        }
        dc.Pop(); dc.Pop();
        var head = X(page.Playhead);
        if (head >= PlotLeft && head <= ActualWidth)
        {
            dc.DrawLine(new Pen(PlayheadBrush, 2), new(head, 22), new(head, ActualHeight));
            var thumb = new StreamGeometry();
            using (var drawing = thumb.Open())
            {
                drawing.BeginFigure(new(head - 5, 16), true, true);
                drawing.LineTo(new(head + 5, 16), true, false);
                drawing.LineTo(new(head + 5, 22), true, false);
                drawing.LineTo(new(head, 28), true, false);
                drawing.LineTo(new(head - 5, 22), true, false);
            }
            dc.DrawGeometry(PlayheadBrush, _seekFocus && IsKeyboardFocused ? new Pen(SelectionBrush, 2) : null, thumb);
        }
        if (IsKeyboardFocused) { dc.DrawRectangle(null, new Pen(SelectionBrush, 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2))); }
    }

    private DrawingGroup CreateWaveformDrawing(ClipCreatorPage page, double plotWidth, double dpi)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            for (var lane = (int)LaneOffset; lane < Math.Min(page.Waveforms.Length, (int)LaneOffset + 5); lane++)
            {
                var data = page.Waveforms[lane];
                var center = 28 + LaneHeight * (lane - LaneOffset + 0.5);
                var name = page.Tracks.FirstOrDefault(t => t.Track.StreamIndex == data.StreamIndex)?.Name ?? "Audio";
                var label = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi)
                { MaxTextWidth = LabelWidth - 8, MaxTextHeight = LaneHeight, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(label, new Point(4, center - label.Height / 2));
                var geometry = new StreamGeometry();
                using (var lines = geometry.Open())
                {
                    for (var i = 0; i < data.Peaks.Length; i++)
                    {
                        var x = X(data.Start + (data.End - data.Start) * i / data.Peaks.Length);
                        var amplitude = data.Peaks[i] * LaneHeight * 0.35;
                        lines.BeginFigure(new(x, center - amplitude), false, false);
                        lines.LineTo(new(x, center + amplitude), true, false);
                    }
                }
                geometry.Freeze();
                dc.DrawLine(new Pen(DividerBrush, 1), new(PlotLeft, center + 20), new(ActualWidth, center + 20));
                dc.PushClip(new RectangleGeometry(new Rect(PlotLeft, 28, plotWidth, Math.Max(0, AudioBottom - 28))));
                dc.PushOpacity(0.5);
                dc.DrawGeometry(null, new Pen(WaveBrush, Math.Max(1, plotWidth / Math.Max(1, data.Peaks.Length))), geometry);
                dc.Pop(); dc.Pop();
            }
        }
        using (var dc = drawing.Append())
        {
            dc.PushClip(new RectangleGeometry(new Rect(PlotLeft, AudioBottom, plotWidth, FilmstripHeight)));
            for (var x = PlotLeft; x < ActualWidth; x += 76)
            {
                var time = Time(Math.Min(ActualWidth, x + 38));
                var thumbnail = page.Thumbnails.LastOrDefault(frame => frame.Time <= time) ?? page.Thumbnails.FirstOrDefault();
                if (thumbnail is not null)
                { dc.DrawImage(thumbnail.Image, new Rect(x, AudioBottom + 3, 75, 42)); }
                else { dc.DrawRectangle(null, new Pen(DividerBrush, 1), new Rect(x, AudioBottom + 3, 75, 42)); }
            }
            dc.Pop();
        }
        // Keep theme brushes live; geometry is immutable, and the group is reused by moving overlays.
        return drawing;
    }
    private double X(double time) => _page is { } page ? PlotLeft + (time - page.ViewportStart) / (page.ViewportEnd - page.ViewportStart) * Math.Max(1, ActualWidth - PlotLeft) : 0;
    private double Time(double x) => _page is { } page ? page.ViewportStart + Math.Clamp((x - PlotLeft) / Math.Max(1, ActualWidth - PlotLeft), 0, 1) * (page.ViewportEnd - page.ViewportStart) : 0;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!BeginPointer(e.GetPosition(this), e.ClickCount)) { return; }
        if (_pointerMode != PointerMode.None && !CaptureMouse()) { EndPointer(commitClick: false); }
        e.Handled = true;
    }
    internal bool BeginPointer(Point point, int clickCount)
    {
        if (_page is not { CanEdit: true } page) { return false; }
        Focus();
        var suppressDoubleClick = _suppressDoubleClick;
        _suppressDoubleClick = false;
        if (!suppressDoubleClick && TrySelectVisibleRange(point, clickCount)) { return true; }
        if (point.X < PlotLeft || point.X > ActualWidth || point.Y < 0 || point.Y > ActualHeight) { return false; }
        _pressPoint = _lastPoint = point;
        _moveStart = page.Start; _moveEnd = page.End;
        _secondsPerPixel = (page.ViewportEnd - page.ViewportStart) / Math.Max(1, ActualWidth - PlotLeft);
        // The ruler always seeks. Grips win over the body, including their expanded hit targets.
        _pointerMode = point.Y < 28 ? PointerMode.Seek
            : Math.Abs(point.X - X(page.Start)) <= 12 ? PointerMode.In
            : Math.Abs(point.X - X(page.End)) <= 12 ? PointerMode.Out
            : point.X > X(page.Start) && point.X < X(page.End) ? PointerMode.PendingMove : PointerMode.Seek;
        _seekFocus = _pointerMode is PointerMode.Seek or PointerMode.PendingMove;
        if (_pointerMode == PointerMode.Seek) { page.BeginScrub(Time(point.X)); }
        else if (_pointerMode != PointerMode.PendingMove)
        { _endHandle = _pointerMode == PointerMode.Out; page.BeginRangeDrag(); }
        UpdatePointerCursor(point);
        return true;
    }
    internal bool TrySelectVisibleRange(Point point, int clickCount)
    {
        if (clickCount != 2 || _page is not { CanEdit: true } page
            || ActualWidth <= PlotLeft || point.X < PlotLeft || point.X > ActualWidth
            || point.Y < 28 || point.Y > ActualHeight
            || (point.Y >= AudioBottom && Math.Abs(point.X - X(page.Start)) > 12 && Math.Abs(point.X - X(page.End)) > 12)) { return false; }
        _pointerMode = PointerMode.None;
        _seekFocus = false;
        _endHandle = false;
        page.EndScrub();
        page.SelectVisibleRange();
        page.EndRangeDrag();
        ReleaseMouseCapture();
        InvalidateVisual();
        return true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (IsMouseCaptured) { MovePointer(point); }
        else { UpdatePointerCursor(point); }
    }
    internal void MovePointer(Point point)
    {
        _lastPoint = point;
        if (_page is not { CanEdit: true } page) { return; }
        if (_pointerMode == PointerMode.PendingMove)
        {
            if (Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) { return; }
            _pointerMode = PointerMode.Move;
            page.BeginRangeDrag();
        }
        if (_pointerMode == PointerMode.Move)
        { page.MoveSelection(_moveStart, _moveEnd, (point.X - _pressPoint.X) * _secondsPerPixel); }
        else if (_pointerMode == PointerMode.Seek) { page.Seek(Math.Round(Time(point.X), 3)); }
        else if (_pointerMode is PointerMode.In or PointerMode.Out) { MoveBoundary(_pointerMode == PointerMode.Out, Math.Round(Time(point.X), 3)); }
        UpdatePointerCursor(point);
    }
    private void UpdatePointerCursor(Point point)
    {
        _lastPoint = point;
        Cursor = _page is not { CanEdit: true } page ? null
            : _pointerMode == PointerMode.Move ? Cursors.Hand
            : _pointerMode is PointerMode.In or PointerMode.Out ? Cursors.SizeWE
            : _pointerMode == PointerMode.Seek ? Cursors.Cross
            : point.X < PlotLeft || point.X > ActualWidth || point.Y < 0 || point.Y > ActualHeight ? null
            : point.Y >= 28 && (Math.Abs(point.X - X(page.Start)) <= 12 || Math.Abs(point.X - X(page.End)) <= 12) ? Cursors.SizeWE
            : point.Y >= 28 && point.X > X(page.Start) && point.X < X(page.End) ? Cursors.Hand : Cursors.Cross;
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) { Cursor = null; }
    }
    internal void EndPointer(bool commitClick)
    {
        var mode = _pointerMode;
        _pointerMode = PointerMode.None;
        if (mode == PointerMode.Move) { _suppressDoubleClick = true; }
        if (commitClick && mode == PointerMode.PendingMove && _page is { CanEdit: true } page)
        { page.Seek(Time(_pressPoint.X)); }
        _page?.EndScrub();
        _page?.EndRangeDrag();
        UpdatePointerCursor(_lastPoint);
    }
    private void FinishPointerForViewportChange()
    {
        if (_pointerMode == PointerMode.None) { return; }
        EndPointer(commitClick: false);
        ReleaseMouseCapture();
    }
    internal void MoveBoundary(bool end, double time)
    {
        if (_page is not { CanEdit: true } page) { return; }
        if (end) { page.SetRange(page.Start, time); }
        else { page.SetRange(time, page.End); }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pointerMode != PointerMode.None) { e.Handled = true; }
        EndPointer(commitClick: true);
        ReleaseMouseCapture();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPointer(commitClick: false);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        FinishPointerForViewportChange();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { LaneOffset += e.Delta > 0 ? -1 : 1; }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { _page?.Pan(e.Delta > 0 ? -0.2 : 0.2); }
        else { _page?.Zoom(e.Delta > 0 ? 0.5 : 2); }
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        FinishPointerForViewportChange();
        switch (e.Key)
        {
            case Key.Tab:
                var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (backwards && _seekFocus || !backwards && !_seekFocus && _endHandle) { return; }
                if (backwards) { if (_endHandle) { _endHandle = false; } else { _seekFocus = true; } }
                else { if (_seekFocus) { _seekFocus = false; _endHandle = false; } else { _endHandle = true; } }
                break;
            case Key.Up: LaneOffset--; break;
            case Key.Down: LaneOffset++; break;
            case Key.PageUp: LaneOffset -= 5; break;
            case Key.PageDown: LaneOffset += 5; break;
            case Key.P: _seekFocus = true; break;
            case Key.Home: _seekFocus = false; _endHandle = false; break;
            case Key.End: _seekFocus = false; _endHandle = true; break;
            case Key.Left:
                if (_seekFocus) { _page?.Seek(_page.Playhead - 0.001); } else { _page?.Nudge(_endHandle, -1); }
                break;
            case Key.Right:
                if (_seekFocus) { _page?.Seek(_page.Playhead + 0.001); } else { _page?.Nudge(_endHandle, 1); }
                break;
            case Key.Add: case Key.OemPlus: _page?.Zoom(0.5); break;
            case Key.Subtract: case Key.OemMinus: _page?.Zoom(2); break;
            default: return;
        }
        e.Handled = true;
        InvalidateVisual();
    }
}
