namespace Encore.App.Library;

/// <summary>Maps source time to a pixel on the clip timeline and back, for the viewport the timeline shows.</summary>
internal readonly record struct TimelineScale(double PlotLeft, double ControlWidth, double ViewportStart, double ViewportEnd)
{
    /// <summary>The width the times spread over, right of the track labels; never under one pixel.</summary>
    public double PlotWidth => Math.Max(1, ControlWidth - PlotLeft);

    public double SecondsPerPixel => (ViewportEnd - ViewportStart) / PlotWidth;

    /// <summary>The x of a time; times outside the viewport fall outside the plot.</summary>
    public double XOf(double time) => PlotLeft + (time - ViewportStart) / (ViewportEnd - ViewportStart) * PlotWidth;

    /// <summary>The time at an x, held to the viewport.</summary>
    public double TimeAt(double x) => ViewportStart + Math.Clamp((x - PlotLeft) / PlotWidth, 0, 1) * (ViewportEnd - ViewportStart);
}
