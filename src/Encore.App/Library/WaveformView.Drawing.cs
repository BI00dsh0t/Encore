using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The timeline's drawing: the ruler, the cached lanes and video strip, then the trim range and the playhead.</summary>
internal sealed partial class WaveformView
{
    // How far apart the ruler's time labels are, at least.
    private const double RulerLabelSpacing = 140;

    // How wide each frame of the video strip is drawn, with its one-pixel gap.
    private const double FrameStep = 76;

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(SurfaceBrush, null, new Rect(RenderSize));
        if (_viewModel is not { Metadata: not null } viewModel || viewModel.ViewportEnd <= viewModel.ViewportStart)
        {
            return;
        }

        var plotWidth = ScaleOf(viewModel).PlotWidth;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        UpdateAutomationName(viewModel);
        DrawRuler(context, viewModel, plotWidth, dpi);
        _waveformDrawing ??= CreateWaveformDrawing(viewModel, plotWidth, dpi);
        context.DrawDrawing(_waveformDrawing);
        DrawCaptions(context, viewModel, dpi);
        DrawRange(context, viewModel, plotWidth);
        var head = XOf(viewModel.Playhead);
        if (head >= PlotLeft && head <= ActualWidth)
        {
            DrawPlayhead(context, head);
        }

        if (IsKeyboardFocused)
        {
            var frame = new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2));
            context.DrawRectangle(null, new Pen(SelectionBrush, 2), frame);
        }
    }

    private static string TrackName(ClipCreatorViewModel viewModel, int streamIndex) =>
        viewModel.Tracks.FirstOrDefault(track => track.Track.StreamIndex == streamIndex)?.Name ?? "Audio";

    // Screen readers hear which handle the keys move and which lanes show.
    private void UpdateAutomationName(ClipCreatorViewModel viewModel)
    {
        var role = _focusedHandle switch
        {
            TimelineHandle.Playhead => "Playhead",
            TimelineHandle.Out => "Out trim",
            _ => "In trim",
        };
        var lanes = viewModel.Waveforms.Skip((int)LaneOffset).Take(VisibleLanes).Select(lane => TrackName(viewModel, lane.StreamIndex));
        AutomationProperties.SetName(this, $"{role}. Audio waveform, video strip and trim selection. " + string.Join(", ", lanes));
    }

    private FormattedText Caption(string text, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, RulerBrush, dpi);

    // Tick lines across the lanes, each with its time; a label that would crowd its neighbours is left out.
    private void DrawRuler(DrawingContext context, ClipCreatorViewModel viewModel, double plotWidth, double dpi)
    {
        var labels = Math.Max(1, (int)(plotWidth / RulerLabelSpacing));
        for (var i = 0; i <= labels; i++)
        {
            var x = PlotLeft + plotWidth * i / labels;
            context.DrawLine(new Pen(DividerBrush, 1), new(x, 20), new(x, AudioBottom));
            var time = viewModel.ViewportStart + (viewModel.ViewportEnd - viewModel.ViewportStart) * i / labels;
            var text = Caption(ClipCreatorViewModel.FormatTime(time), dpi);
            if (i > 0 && plotWidth < text.Width * 2 + 8)
            {
                continue;
            }

            var labelX = i == 0 ? x : i == labels ? x - text.Width : x - text.Width / 2;
            context.DrawText(text, new Point(Math.Max(PlotLeft, labelX), 2));
        }
    }

    private void DrawCaptions(DrawingContext context, ClipCreatorViewModel viewModel, double dpi)
    {
        var videoLabel = Caption("Video", dpi);
        context.DrawText(videoLabel, new Point(4, AudioBottom + (FilmstripHeight - videoLabel.Height) / 2));
        if (viewModel.Waveforms.Length == 0 && !viewModel.Tracks.Any(track => track.Included))
        {
            context.DrawText(Caption("No audio tracks selected", dpi), new Point(4, 30));
        }
    }

    // The lanes and the video strip move only with the viewport, so they are drawn once and reused.
    private DrawingGroup CreateWaveformDrawing(ClipCreatorViewModel viewModel, double plotWidth, double dpi)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            for (var lane = (int)LaneOffset; lane < Math.Min(viewModel.Waveforms.Length, (int)LaneOffset + VisibleLanes); lane++)
            {
                DrawLane(context, viewModel, lane, plotWidth, dpi);
            }
        }

        using (var context = drawing.Append())
        {
            DrawFilmstrip(context, viewModel, plotWidth);
        }

        return drawing;
    }

    private void DrawLane(DrawingContext context, ClipCreatorViewModel viewModel, int lane, double plotWidth, double dpi)
    {
        var data = viewModel.Waveforms[lane];
        var center = RulerHeight + LaneHeight * (lane - LaneOffset + 0.5);
        var label = Caption(TrackName(viewModel, data.StreamIndex), dpi);
        label.MaxTextWidth = LabelWidth - 8;
        label.MaxTextHeight = LaneHeight;
        label.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(label, new Point(4, center - label.Height / 2));
        var peaks = PeakGeometry(data, center);
        context.DrawLine(new Pen(DividerBrush, 1), new(PlotLeft, center + 20), new(ActualWidth, center + 20));
        context.PushClip(new RectangleGeometry(new Rect(PlotLeft, RulerHeight, plotWidth, Math.Max(0, AudioBottom - RulerHeight))));
        context.PushOpacity(0.5);
        context.DrawGeometry(null, new Pen(WaveBrush, Math.Max(1, plotWidth / Math.Max(1, data.Peaks.Length))), peaks);
        context.Pop();
        context.Pop();
    }

    // One vertical stroke per peak, as tall as the peak, centred on the lane.
    private StreamGeometry PeakGeometry(ClipWaveform data, double center)
    {
        var geometry = new StreamGeometry();
        using (var lines = geometry.Open())
        {
            for (var i = 0; i < data.Peaks.Length; i++)
            {
                var x = XOf(data.Start + (data.End - data.Start) * i / data.Peaks.Length);
                var amplitude = data.Peaks[i] * LaneHeight * 0.35;
                lines.BeginFigure(new(x, center - amplitude), false, false);
                lines.LineTo(new(x, center + amplitude), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // Each slot shows the latest thumbnail at or before its middle; an empty frame until there is one.
    private void DrawFilmstrip(DrawingContext context, ClipCreatorViewModel viewModel, double plotWidth)
    {
        context.PushClip(new RectangleGeometry(new Rect(PlotLeft, AudioBottom, plotWidth, FilmstripHeight)));
        for (var x = PlotLeft; x < ActualWidth; x += FrameStep)
        {
            var time = TimeAt(Math.Min(ActualWidth, x + FrameStep / 2));
            var thumbnail = viewModel.Thumbnails.LastOrDefault(frame => frame.Time <= time) ?? viewModel.Thumbnails.FirstOrDefault();
            var slot = new Rect(x, AudioBottom + 3, FrameStep - 1, 42);
            if (thumbnail is not null)
            {
                context.DrawImage(thumbnail.Image, slot);
            }
            else
            {
                context.DrawRectangle(null, new Pen(DividerBrush, 1), slot);
            }
        }

        context.Pop();
    }

    // The veil dims what lies outside the trim range; the range and its grips fade while it can't be edited.
    private void DrawRange(DrawingContext context, ClipCreatorViewModel viewModel, double plotWidth)
    {
        var left = XOf(viewModel.Start);
        var right = XOf(viewModel.End);
        var below = Math.Max(0, ActualHeight - RulerHeight);
        context.PushClip(new RectangleGeometry(new Rect(PlotLeft, RulerHeight, plotWidth, below)));
        context.PushOpacity(0.55);
        context.DrawRectangle(VeilBrush, null, new Rect(PlotLeft, RulerHeight, Math.Clamp(left - PlotLeft, 0, plotWidth), below));
        var after = Math.Clamp(right, PlotLeft, ActualWidth);
        context.DrawRectangle(VeilBrush, null, new Rect(after, RulerHeight, ActualWidth - after, below));
        context.Pop();
        context.PushOpacity(viewModel.CanEdit ? 1 : 0.45);
        DrawSelection(context, left, right);
        context.Pop();
        context.Pop();
    }

    private void DrawSelection(DrawingContext context, double left, double right)
    {
        var selection = new Rect(left, RulerHeight, Math.Max(0, right - left), Math.Max(1, ActualHeight - 31));
        context.PushOpacity(0.09);
        context.DrawRoundedRectangle(SelectionBrush, null, selection, 9, 9);
        context.Pop();
        context.DrawRoundedRectangle(null, new Pen(SelectionBrush, 2), selection, 9, 9);
        foreach (var x in new[] { left, right })
        {
            DrawGrip(context, x, x == left ? TimelineHandle.In : TimelineHandle.Out);
        }
    }

    // A grip at each end of the range; the one the keys move gets a ring while the timeline has focus.
    private void DrawGrip(DrawingContext context, double x, TimelineHandle handle)
    {
        if (x < PlotLeft || x > ActualWidth)
        {
            return;
        }

        var grip = new Rect(x - 6, RulerHeight, 12, Math.Max(1, ActualHeight - 31));
        context.DrawRoundedRectangle(SelectionBrush, null, grip, 7, 7);
        context.DrawLine(new Pen(GripBrush, 2), new(x, grip.Top + (grip.Height - 17) / 2), new(x, grip.Top + (grip.Height + 17) / 2));
        if (IsKeyboardFocused && _focusedHandle == handle)
        {
            context.DrawRoundedRectangle(null, new Pen(GripBrush, 2), new Rect(x - 9, 29, 18, Math.Max(1, ActualHeight - 33)), 7, 7);
        }
    }

    // A line down the timeline under a pointed marker on the ruler, outlined while the keys move it.
    private void DrawPlayhead(DrawingContext context, double head)
    {
        context.DrawLine(new Pen(PlayheadBrush, 2), new(head, 22), new(head, ActualHeight));
        var marker = new StreamGeometry();
        using (var outline = marker.Open())
        {
            outline.BeginFigure(new(head - 5, 16), true, true);
            outline.LineTo(new(head + 5, 16), true, false);
            outline.LineTo(new(head + 5, 22), true, false);
            outline.LineTo(new(head, 28), true, false);
            outline.LineTo(new(head - 5, 22), true, false);
        }

        var ring = _focusedHandle == TimelineHandle.Playhead && IsKeyboardFocused ? new Pen(SelectionBrush, 2) : null;
        context.DrawGeometry(PlayheadBrush, ring, marker);
    }
}
