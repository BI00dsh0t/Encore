namespace Encore.Media;

/// <summary>One screen's rectangle in a composited canvas, in canvas pixels.</summary>
public readonly record struct CanvasRect(int X, int Y, int Width, int Height);

/// <summary>
/// The GPU side of a <see cref="MonitorCompositor"/>: the canvas texture it draws into and hands on.
/// <see cref="Blit"/> and <see cref="Clear"/> come one at a time under the compositor's drawing lock;
/// <see cref="Present"/> comes one at a time under its hand-off lock, so it may run beside a draw.
/// </summary>
public interface ICanvasSurface
{
    /// <summary>
    /// Draws a source frame (<paramref name="texture"/>, valid only during the call, its content
    /// <paramref name="contentWidth"/> × <paramref name="contentHeight"/>) into <paramref name="area"/>,
    /// scaled into it when the sizes differ (a mode change).
    /// </summary>
    void Blit(nint texture, int contentWidth, int contentHeight, CanvasRect area);

    /// <summary>Fills <paramref name="area"/> with black.</summary>
    void Clear(CanvasRect area);

    /// <summary>Hands the canvas on as one frame stamped <paramref name="qpc"/>.</summary>
    void Present(long qpc);
}

/// <summary>
/// A multi-monitor Monitor Recording's compositor (AD-25, story 11.3). Each source's frames are drawn
/// into its rectangle as they arrive, so the canvas always holds every source's latest frame. The
/// canvas is handed to the pacer at most once per output slot of the compositor's own slot clock,
/// never once per source frame, so its stamps always increase and the hand-off never fills, whatever
/// the sources' rates: the first frame drawn in a new slot hands it on at once, and the clock's
/// <see cref="Tick"/> hands on a slot whose change came too late for that. A slot in which nothing
/// changed hands nothing on: the pacer repeats the last canvas. The hand-off runs outside the drawing
/// lock, so a hand-off that waits on the encoder never holds up the sources. A removed source's
/// rectangle turns black, and the canvas never changes size.
/// </summary>
public sealed class MonitorCompositor
{
    private readonly ICanvasSurface _surface;
    private readonly CanvasRect[] _areas;
    private readonly bool[] _live;
    private readonly long _qpcFrequency;
    private readonly Lock _gate = new();
    private readonly Lock _handOff = new();
    private long _lastStamp = long.MinValue;
    private int _fps;
    private long _origin = long.MinValue;
    private long _lastSlot = long.MinValue;
    private bool _dirty;
    private int _liveCount;

    public MonitorCompositor(ICanvasSurface surface, IReadOnlyList<CanvasRect> areas, long qpcFrequency, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfZero(areas.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _surface = surface;
        _areas = [.. areas];
        _live = [.. areas.Select(_ => true)];
        _liveCount = _areas.Length;
        _qpcFrequency = qpcFrequency;
        _fps = fps;
    }

    /// <summary>The sources not yet removed.</summary>
    public int Live
    {
        get
        {
            lock (_gate)
            {
                return _liveCount;
            }
        }
    }

    /// <summary>Canvases handed on so far.</summary>
    public long Presents { get; private set; }

    /// <summary>Source frames drawn so far.</summary>
    public long Blits { get; private set; }

    /// <summary>Sets the output frame rate: the slot clock's period from the next tick.</summary>
    public void SetFps(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        lock (_gate)
        {
            _fps = fps;
            _origin = long.MinValue;
            _lastSlot = long.MinValue;
        }
    }

    /// <summary>
    /// Draws source <paramref name="source"/>'s new frame into its rectangle, then, at
    /// <paramref name="nowQpc"/>, hands the canvas on when this slot hasn't had one yet. A removed
    /// source's frame is ignored. Returns whether the canvas was handed on.
    /// </summary>
    public bool OnFrame(int source, nint texture, int contentWidth, int contentHeight, long nowQpc)
    {
        lock (_gate)
        {
            if (!_live[source])
            {
                return false;
            }

            _surface.Blit(texture, contentWidth, contentHeight, _areas[source]);
            Blits++;
            _dirty = true;
        }

        return TryHandOn(nowQpc);
    }

    /// <summary>
    /// Removes source <paramref name="source"/>: its rectangle turns black (its last frame isn't held,
    /// which would look live) at the next slot. Returns the sources left.
    /// </summary>
    public int Remove(int source)
    {
        lock (_gate)
        {
            if (_live[source])
            {
                _live[source] = false;
                _liveCount--;
                _surface.Clear(_areas[source]);
                _dirty = true;
            }

            return _liveCount;
        }
    }

    /// <summary>
    /// The slot clock, at <paramref name="nowQpc"/>: hands the canvas on when it changed since the last
    /// hand-off and this slot hasn't had one. Returns whether it did.
    /// </summary>
    public bool Tick(long nowQpc) => TryHandOn(nowQpc);

    /// <summary>
    /// Hands the canvas on, stamped <paramref name="nowQpc"/> (or just after the last stamp), when it
    /// changed and this slot hasn't had a hand-off. One hand-off at a time: while another runs, this one
    /// leaves the change for the next frame or tick.
    /// </summary>
    private bool TryHandOn(long nowQpc)
    {
        if (!_handOff.TryEnter())
        {
            return false;
        }

        try
        {
            long stamp;
            lock (_gate)
            {
                if (_origin == long.MinValue)
                {
                    _origin = nowQpc;
                }

                var slot = (long)((Int128)(nowQpc - _origin) * _fps / _qpcFrequency);
                if (!_dirty || slot <= _lastSlot)
                {
                    return false;
                }

                _lastSlot = slot;
                _dirty = false;
                stamp = Math.Max(nowQpc, _lastStamp + 1);
                _lastStamp = stamp;
                Presents++;
            }

            _surface.Present(stamp);
            return true;
        }
        finally
        {
            _handOff.Exit();
        }
    }

    /// <summary>The QPC of the slot after the one <paramref name="nowQpc"/> is in: when the clock next ticks.</summary>
    public long NextTick(long nowQpc)
    {
        lock (_gate)
        {
            if (_origin == long.MinValue)
            {
                return nowQpc;
            }

            var slot = (long)((Int128)(nowQpc - _origin) * _fps / _qpcFrequency);
            return _origin + (long)((Int128)(slot + 1) * _qpcFrequency / _fps);
        }
    }
}
