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
    public static readonly DependencyProperty LaneOffsetProperty = DependencyProperty.Register(nameof(LaneOffset), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, (d, value) => Math.Clamp(Math.Round((double)value), 0, ((WaveformView)d).ScrollMaximum)));
    public static readonly DependencyProperty ScrollMaximumProperty = DependencyProperty.Register(nameof(ScrollMaximum), typeof(double), typeof(WaveformView), new PropertyMetadata(0d));
    public double LaneOffset { get => (double)GetValue(LaneOffsetProperty); set => SetValue(LaneOffsetProperty, value); }
    public double ScrollMaximum { get => (double)GetValue(ScrollMaximumProperty); private set => SetValue(ScrollMaximumProperty, value); }
    private const double LabelWidth = 100;
    private const double LaneHeight = 24;
    private double PlotLeft => Math.Min(LabelWidth, Math.Max(0, ActualWidth - 1));
    private ClipCreatorPage? _page;
    private int _handle;
    private bool _endHandle;

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

    private void Attach(ClipCreatorPage? page)
    {
        if (_page is not null) { _page.PropertyChanged -= Changed; }
        _page = page;
        if (_page is not null) { _page.PropertyChanged += Changed; }
        InvalidateMeasure();
        InvalidateVisual();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipCreatorPage.Waveforms) or nameof(ClipCreatorPage.Metadata) or nameof(ClipCreatorPage.EffectiveText)) { InvalidateMeasure(); }
        InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Math.Max(_page?.Waveforms.Length ?? 0, _page?.Tracks.Count(t => t.Included) ?? 0);
        ScrollMaximum = Math.Max(0, lanes - 5);
        CoerceValue(LaneOffsetProperty);
        return new Size(double.IsPositiveInfinity(availableSize.Width) ? 0 : availableSize.Width, 28 + Math.Max(1, Math.Min(5, lanes)) * LaneHeight);
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
        var pen = new Pen(RulerBrush, 1);
        var lanes = page.Waveforms;
        System.Windows.Automation.AutomationProperties.SetName(this, "Audio waveform and trim selection. " + string.Join(", ", lanes.Skip((int)LaneOffset).Take(5).Select(lane => page.Tracks.FirstOrDefault(t => t.Track.StreamIndex == lane.StreamIndex)?.Name ?? "Audio")));
        var laneHeight = LaneHeight;
        var plotWidth = Math.Max(1, ActualWidth - PlotLeft);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string value, double x, double y) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi), new Point(Math.Clamp(x, 0, Math.Max(0, ActualWidth - 100)), y));
        var rulerLabels = Math.Max(1, (int)(plotWidth / 150));
        for (var i = 0; i <= rulerLabels; i++)
        {
            var x = PlotLeft + plotWidth * i / rulerLabels;
            dc.DrawLine(pen, new(x, 20), new(x, ActualHeight));
            var text = new FormattedText(ClipCreatorPage.FormatTime(page.ViewportStart + (page.ViewportEnd - page.ViewportStart) * i / rulerLabels),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi);
            // If even two full timestamps cannot fit, keep only the visible viewport start.
            if (i > 0 && plotWidth < text.Width * 2 + 8) { continue; }
            var labelX = i == 0 ? x : i == rulerLabels ? x - text.Width : x - text.Width / 2;
            dc.DrawText(text, new Point(Math.Max(PlotLeft, labelX), 2));
        }
        for (var lane = (int)LaneOffset; lane < Math.Min(lanes.Length, (int)LaneOffset + 5); lane++)
        {
            var data = lanes[lane];
            var center = 28 + laneHeight * (lane - LaneOffset + 0.5);
            var peaks = new Pen(WaveBrush, Math.Max(1, plotWidth / Math.Max(1, data.Peaks.Length)));
            var name = page.Tracks.FirstOrDefault(t => t.Track.StreamIndex == data.StreamIndex)?.Name ?? "Audio";
            var label = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi)
            { MaxTextWidth = LabelWidth - 8, MaxTextHeight = LaneHeight, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(label, new Point(4, 28 + laneHeight * (lane - LaneOffset)));
            for (var i = 0; i < data.Peaks.Length; i++)
            {
                var x = PlotLeft + plotWidth * i / data.Peaks.Length;
                var amplitude = data.Peaks[i] * laneHeight * 0.35;
                dc.DrawLine(peaks, new(x, center - amplitude), new(x, center + amplitude));
            }
        }
        if (lanes.Length == 0) { Label(page.Tracks.Any(t => t.Included) ? "Loading source waveform…" : "No audio tracks selected", 8, 30); }
        var left = X(page.Start);
        var right = X(page.End);
        dc.PushOpacity(page.CanEdit ? 1 : 0.45);
        var fill = SelectionBrush.Clone(); fill.Opacity = 0.15;
        dc.DrawRectangle(fill, new Pen(SelectionBrush, 1), new Rect(new Point(Math.Clamp(left, PlotLeft, ActualWidth), 28), new Point(Math.Clamp(right, PlotLeft, ActualWidth), ActualHeight)));
        foreach (var x in new[] { left, right })
        {
            if (x >= PlotLeft && x <= ActualWidth)
            {
                var handle = new Rect(x - 5, 28, 10, Math.Max(1, ActualHeight - 28));
                dc.DrawRoundedRectangle(SelectionBrush, null, handle, 3, 3);
                dc.DrawLine(new Pen(SurfaceBrush, 2), new(x, handle.Top + (handle.Height - 15) / 2), new(x, handle.Top + (handle.Height + 15) / 2));
                if (IsKeyboardFocused && (x == left ? !_endHandle : _endHandle))
                { dc.DrawRoundedRectangle(null, new Pen(RulerBrush, 2), new Rect(x - 8, 25, 16, Math.Max(1, ActualHeight - 26)), 4, 4); }
            }
        }
        dc.Pop();
        var head = X(page.Playhead);
        if (head >= PlotLeft && head <= ActualWidth) { dc.DrawLine(new Pen(RulerBrush, 2), new(head, 22), new(head, ActualHeight)); }
        if (IsKeyboardFocused) { dc.DrawRectangle(null, new Pen(SelectionBrush, 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2))); }
    }
    private double X(double time) => _page is { } page ? PlotLeft + (time - page.ViewportStart) / (page.ViewportEnd - page.ViewportStart) * Math.Max(1, ActualWidth - PlotLeft) : 0;
    private double Time(double x) => _page is { } page ? page.ViewportStart + Math.Clamp((x - PlotLeft) / Math.Max(1, ActualWidth - PlotLeft), 0, 1) * (page.ViewportEnd - page.ViewportStart) : 0;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_page is not { CanEdit: true } page) { return; }
        Focus();
        var x = e.GetPosition(this).X;
        _handle = Math.Abs(x - X(page.Start)) <= 12 ? 1 : Math.Abs(x - X(page.End)) <= 12 ? 2 : 0;
        if (_handle != 0) { _endHandle = _handle == 2; CaptureMouse(); }
        else { page.Seek(Time(x)); }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && _page is { CanEdit: true } page)
        {
            var time = Math.Round(Time(e.GetPosition(this).X), 3);
            if (_handle != 0) { MoveBoundary(_handle == 2, time); }
        }
    }
    internal void MoveBoundary(bool end, double time)
    {
        if (_page is not { CanEdit: true } page) { return; }
        if (end) { page.SetRange(page.Start, time); }
        else { page.SetRange(time, page.End); }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); _handle = 0; ReleaseMouseCapture(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { LaneOffset += e.Delta > 0 ? -1 : 1; }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { _page?.Pan(e.Delta > 0 ? -0.2 : 0.2); }
        else { _page?.Zoom(e.Delta > 0 ? 0.5 : 2); }
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Up: LaneOffset--; break;
            case Key.Down: LaneOffset++; break;
            case Key.PageUp: LaneOffset -= 5; break;
            case Key.PageDown: LaneOffset += 5; break;
            case Key.Home: _endHandle = false; break;
            case Key.End: _endHandle = true; break;
            case Key.Left: _page?.Nudge(_endHandle, -1); break;
            case Key.Right: _page?.Nudge(_endHandle, 1); break;
            case Key.Add: case Key.OemPlus: _page?.Zoom(0.5); break;
            case Key.Subtract: case Key.OemMinus: _page?.Zoom(2); break;
            default: return;
        }
        e.Handled = true;
        InvalidateVisual();
    }
}
