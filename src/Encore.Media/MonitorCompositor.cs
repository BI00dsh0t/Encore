namespace Encore.Media;

/// <summary>One screen's rectangle in a composited canvas, in canvas pixels.</summary>
public readonly record struct CanvasRect(int X, int Y, int Width, int Height);

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

/// <summary>A multi-monitor recording's compositor: one canvas, handed on at most once per slot. Rules: docs/code-notes.md.</summary>
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

    /// <summary>Draws a source's new frame, then hands the canvas on when this slot hasn't had one; returns whether it did.</summary>
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

    /// <summary>Removes a source: its rectangle turns black at the next slot, as a held frame would look live.</summary>
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

    /// <summary>The slot clock: hands the canvas on when it changed and this slot hasn't had a hand-off; returns whether it did.</summary>
    public bool Tick(long nowQpc) => TryHandOn(nowQpc);

    /// <summary>Hands the canvas on when it changed and this slot had none; while another hand-off runs, the change waits.</summary>
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

    /// <summary>When the clock next ticks: the QPC of the slot after the one nowQpc is in.</summary>
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
