using System.Windows;
using System.Windows.Input;

namespace Encore.App.Library;

/// <summary>The timeline's pointer and keys: they seek, drag the trim grips, move the range and zoom.</summary>
internal sealed partial class WaveformView
{
    // How close to a grip, in pixels, a press takes the grip rather than the body.
    private const double GripReach = 12;

    private PointerMode _pointerMode;
    private Point _pressPoint;
    private Point _lastPoint;
    private double _moveStart;
    private double _moveEnd;
    private double _secondsPerPixel;
    private bool _suppressDoubleClick;
    private TimelineHandle _focusedHandle = TimelineHandle.Playhead;

    // What a press is doing: dragging a grip, scrubbing, or a press on the range that becomes a move once it drags.
    private enum PointerMode
    {
        None,
        In,
        Out,
        Seek,
        PendingMove,
        Move,
    }

    /// <summary>Starts a press: a double click selects the visible range; else it seeks, grabs a grip or the range.</summary>
    internal bool BeginPointer(Point point, int clickCount)
    {
        if (_viewModel is not { CanEdit: true } viewModel)
        {
            return false;
        }

        Focus();
        var suppressDoubleClick = _suppressDoubleClick;
        _suppressDoubleClick = false;
        if (!suppressDoubleClick && TrySelectVisibleRange(point, clickCount))
        {
            return true;
        }

        if (IsOutside(point))
        {
            return false;
        }

        _pressPoint = _lastPoint = point;
        _moveStart = viewModel.Start;
        _moveEnd = viewModel.End;
        _secondsPerPixel = ScaleOf(viewModel).SecondsPerPixel;
        _pointerMode = ModeAt(point, viewModel);
        _focusedHandle = _pointerMode switch
        {
            PointerMode.In => TimelineHandle.In,
            PointerMode.Out => TimelineHandle.Out,
            _ => TimelineHandle.Playhead,
        };
        if (_pointerMode == PointerMode.Seek)
        {
            viewModel.BeginScrub(TimeAt(point.X));
        }
        else if (_pointerMode != PointerMode.PendingMove)
        {
            viewModel.BeginRangeDrag();
        }

        UpdatePointerCursor(point);
        return true;
    }

    /// <summary>A double click on the lanes, or on a grip in the video strip, selects the visible time range.</summary>
    internal bool TrySelectVisibleRange(Point point, int clickCount)
    {
        if (clickCount != 2 || _viewModel is not { CanEdit: true } viewModel || !CanSelectVisibleRangeAt(point, viewModel))
        {
            return false;
        }

        _pointerMode = PointerMode.None;
        _focusedHandle = TimelineHandle.In;
        viewModel.EndScrub();
        viewModel.SelectVisibleRange();
        viewModel.EndRangeDrag();
        ReleaseMouseCapture();
        InvalidateVisual();
        return true;
    }

    /// <summary>Follows the pointer during a press: scrubs, drags a grip, or moves the range once the press has dragged.</summary>
    internal void MovePointer(Point point)
    {
        _lastPoint = point;
        if (_viewModel is not { CanEdit: true } viewModel)
        {
            return;
        }

        if (_pointerMode == PointerMode.PendingMove)
        {
            if (IsWithinDragDistance(point))
            {
                return;
            }

            _pointerMode = PointerMode.Move;
            viewModel.BeginRangeDrag();
        }

        if (_pointerMode == PointerMode.Move)
        {
            viewModel.MoveSelection(_moveStart, _moveEnd, (point.X - _pressPoint.X) * _secondsPerPixel);
        }
        else if (_pointerMode == PointerMode.Seek)
        {
            viewModel.Seek(Math.Round(TimeAt(point.X), 3));
        }
        else if (_pointerMode is PointerMode.In or PointerMode.Out)
        {
            MoveBoundary(_pointerMode == PointerMode.Out, Math.Round(TimeAt(point.X), 3));
        }

        UpdatePointerCursor(point);
    }

    /// <summary>Ends a press; a press on the range that never dragged was a click, and seeks when committed.</summary>
    internal void EndPointer(bool commitClick)
    {
        var mode = _pointerMode;
        _pointerMode = PointerMode.None;
        if (mode == PointerMode.Move)
        {
            _suppressDoubleClick = true;
        }

        if (commitClick && mode == PointerMode.PendingMove && _viewModel is { CanEdit: true } viewModel)
        {
            viewModel.Seek(TimeAt(_pressPoint.X));
        }

        _viewModel?.EndScrub();
        _viewModel?.EndRangeDrag();
        UpdatePointerCursor(_lastPoint);
    }

    /// <summary>Moves the In or the Out trim to a time.</summary>
    internal void MoveBoundary(bool end, double time)
    {
        if (_viewModel is not { CanEdit: true } viewModel)
        {
            return;
        }

        if (end)
        {
            viewModel.SetRange(viewModel.Start, time);
        }
        else
        {
            viewModel.SetRange(time, viewModel.End);
        }
    }

    /// <summary>The handle Tab moves to, or null when Tab leaves the timeline: forward Playhead, In, Out.</summary>
    internal static TimelineHandle? NextHandle(TimelineHandle handle, bool backwards) => (handle, backwards) switch
    {
        (TimelineHandle.Playhead, false) => TimelineHandle.In,
        (TimelineHandle.In, false) => TimelineHandle.Out,
        (TimelineHandle.Out, true) => TimelineHandle.In,
        (TimelineHandle.In, true) => TimelineHandle.Playhead,
        _ => null,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!BeginPointer(e.GetPosition(this), e.ClickCount))
        {
            return;
        }

        if (_pointerMode != PointerMode.None && !CaptureMouse())
        {
            EndPointer(commitClick: false);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (IsMouseCaptured)
        {
            MovePointer(point);
        }
        else
        {
            UpdatePointerCursor(point);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured)
        {
            Cursor = null;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pointerMode != PointerMode.None)
        {
            e.Handled = true;
        }

        EndPointer(commitClick: true);
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPointer(commitClick: false);
    }

    // Ctrl scrolls the lanes, Shift pans, and the wheel alone zooms.
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        FinishPointerForViewportChange();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            LaneOffset += e.Delta > 0 ? -1 : 1;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _viewModel?.Pan(e.Delta > 0 ? -0.2 : 0.2);
        }
        else
        {
            _viewModel?.Zoom(e.Delta > 0 ? 0.5 : 2);
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        FinishPointerForViewportChange();
        if (HandleKey(e.Key))
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    // The timeline's keys; false for a key it leaves alone, such as Tab past the last handle.
    private bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Tab:
                return MoveHandleFocus(backwards: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            case Key.Up:
                LaneOffset--;
                break;
            case Key.Down:
                LaneOffset++;
                break;
            case Key.PageUp:
                LaneOffset -= VisibleLanes;
                break;
            case Key.PageDown:
                LaneOffset += VisibleLanes;
                break;
            case Key.P:
                _focusedHandle = TimelineHandle.Playhead;
                break;
            case Key.Home:
                _focusedHandle = TimelineHandle.In;
                break;
            case Key.End:
                _focusedHandle = TimelineHandle.Out;
                break;
            case Key.Left:
                StepFocusedHandle(-1);
                break;
            case Key.Right:
                StepFocusedHandle(1);
                break;
            case Key.Add or Key.OemPlus:
                _viewModel?.Zoom(0.5);
                break;
            case Key.Subtract or Key.OemMinus:
                _viewModel?.Zoom(2);
                break;
            default:
                return false;
        }

        return true;
    }

    private bool MoveHandleFocus(bool backwards)
    {
        if (NextHandle(_focusedHandle, backwards) is not { } next)
        {
            return false;
        }

        _focusedHandle = next;
        return true;
    }

    // Left and Right move the focused handle by one millisecond.
    private void StepFocusedHandle(int direction)
    {
        if (_focusedHandle == TimelineHandle.Playhead)
        {
            _viewModel?.Seek(_viewModel.Playhead + direction * 0.001);
        }
        else
        {
            _viewModel?.Nudge(_focusedHandle == TimelineHandle.Out, direction);
        }
    }

    private void UpdatePointerCursor(Point point)
    {
        _lastPoint = point;
        Cursor = _viewModel is { CanEdit: true } viewModel ? CursorAt(point, viewModel) : null;
    }

    // During a press the cursor says what it does; hovering, it says what a press there would do.
    private Cursor? CursorAt(Point point, ClipCreatorViewModel viewModel)
    {
        switch (_pointerMode)
        {
            case PointerMode.Move:
                return Cursors.Hand;
            case PointerMode.In or PointerMode.Out:
                return Cursors.SizeWE;
            case PointerMode.Seek:
                return Cursors.Cross;
        }

        if (IsOutside(point))
        {
            return null;
        }

        if (point.Y >= RulerHeight && IsNearGrip(point.X, viewModel))
        {
            return Cursors.SizeWE;
        }

        return point.Y >= RulerHeight && IsOnRange(point.X, viewModel) ? Cursors.Hand : Cursors.Cross;
    }

    // The ruler always seeks. Grips win over the range's body, including their wider reach.
    private PointerMode ModeAt(Point point, ClipCreatorViewModel viewModel)
    {
        if (point.Y < RulerHeight)
        {
            return PointerMode.Seek;
        }

        if (Math.Abs(point.X - XOf(viewModel.Start)) <= GripReach)
        {
            return PointerMode.In;
        }

        if (Math.Abs(point.X - XOf(viewModel.End)) <= GripReach)
        {
            return PointerMode.Out;
        }

        return IsOnRange(point.X, viewModel) ? PointerMode.PendingMove : PointerMode.Seek;
    }

    private bool CanSelectVisibleRangeAt(Point point, ClipCreatorViewModel viewModel)
    {
        if (ActualWidth <= PlotLeft || point.X < PlotLeft || point.X > ActualWidth || point.Y < RulerHeight || point.Y > ActualHeight)
        {
            return false;
        }

        return point.Y < AudioBottom || IsNearGrip(point.X, viewModel);
    }

    private bool IsOutside(Point point) => point.X < PlotLeft || point.X > ActualWidth || point.Y < 0 || point.Y > ActualHeight;

    private bool IsNearGrip(double x, ClipCreatorViewModel viewModel) =>
        Math.Abs(x - XOf(viewModel.Start)) <= GripReach || Math.Abs(x - XOf(viewModel.End)) <= GripReach;

    private bool IsOnRange(double x, ClipCreatorViewModel viewModel) => x > XOf(viewModel.Start) && x < XOf(viewModel.End);

    // Until the pointer moves this far, a press on the range is still a click.
    private bool IsWithinDragDistance(Point point) =>
        Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
        && Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance;

    // A viewport change mid-press would move the press under the pointer, so the press ends instead.
    private void FinishPointerForViewportChange()
    {
        if (_pointerMode == PointerMode.None)
        {
            return;
        }

        EndPointer(commitClick: false);
        ReleaseMouseCapture();
    }
}
