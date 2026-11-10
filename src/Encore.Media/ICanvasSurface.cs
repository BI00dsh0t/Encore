namespace Encore.Media;

/// <summary>The compositor's GPU side: Blit and Clear come under the drawing lock, Present under the hand-off lock.</summary>
public interface ICanvasSurface
{
    /// <summary>Draws a source frame, valid only during the call, into its area, scaled when the sizes differ.</summary>
    void Blit(nint texture, int contentWidth, int contentHeight, CanvasRect area);

    /// <summary>Fills <paramref name="area"/> with black.</summary>
    void Clear(CanvasRect area);

    /// <summary>Hands the canvas on as one frame stamped <paramref name="qpc"/>.</summary>
    void Present(long qpc);
}
