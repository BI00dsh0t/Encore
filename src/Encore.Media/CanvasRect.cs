namespace Encore.Media;

/// <summary>One screen's rectangle in a composited canvas, in canvas pixels.</summary>
public readonly record struct CanvasRect(int X, int Y, int Width, int Height);
