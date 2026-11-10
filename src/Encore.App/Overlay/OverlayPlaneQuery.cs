namespace Encore.App.Overlay;

/// <summary>Asks, off the UI thread, whether the Target Game's monitor has overlay planes, and remembers the answer. UI thread.</summary>
internal sealed class OverlayPlaneQuery
{
    private readonly TimeProvider _time;
    private readonly Func<int?, bool?> _overlayPlanes;
    private readonly Action<Action> _background;
    private readonly Action<Action> _post;
    private readonly Action _answered;

    // Whether a query is out, and for which Target Game process.
    private bool _askOut;
    private int? _askFor;

    // Bumped by a display change, so an answer asked before it is dropped.
    private int _generation;
    private bool _inQuery;
    private (int? ProcessId, bool? Planes, DateTimeOffset At)? _planes;

    /// <summary>The query runs on background; post returns to the UI thread, where answered is called for a late answer.</summary>
    public OverlayPlaneQuery(
        TimeProvider time,
        Func<int?, bool?> overlayPlanes,
        Action<Action> background,
        Action<Action> post,
        Action answered)
    {
        _time = time;
        _overlayPlanes = overlayPlanes;
        _background = background;
        _post = post;
        _answered = answered;
    }

    /// <summary>The monitors changed: forget the answer, and drop one still on its way.</summary>
    public void Reset()
    {
        _planes = null;
        _askOut = false;
        _generation++;
    }

    /// <summary>The answer for a process, or null while unknown; asks once per Target Game and after a display change.</summary>
    public bool? Planes(int? processId)
    {
        var now = _time.GetUtcNow();
        var stale = _planes is not { } known || known.ProcessId != processId
            || (known.Planes is null && now - known.At >= OverlayViewModel.UnknownPlanesRetry);
        if (stale && !(_askOut && _askFor == processId))
        {
            Ask(processId);
        }

        return _planes is { } answer && answer.ProcessId == processId ? answer.Planes : null;
    }

    private void Ask(int? processId)
    {
        (_askOut, _askFor) = (true, processId);
        var generation = _generation;
        _inQuery = true;
        try
        {
            _background(() =>
            {
                var planes = Query(processId);
                _post(() => Answered(generation, processId, planes));
            });
        }
        finally
        {
            _inQuery = false;
        }
    }

    private bool? Query(int? processId)
    {
        try
        {
            return _overlayPlanes(processId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Runs on the UI thread. An answer that comes back inline, inside Planes, is read by the caller that asked.
    private void Answered(int generation, int? processId, bool? planes)
    {
        if (generation != _generation)
        {
            return;
        }

        if (_askOut && _askFor == processId)
        {
            _askOut = false;
        }

        _planes = (processId, planes, _time.GetUtcNow());
        if (!_inQuery)
        {
            _answered();
        }
    }
}
