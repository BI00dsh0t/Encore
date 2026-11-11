using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Encore.Platform.Capture;

/// <summary>A capture thread's sleep: a high-resolution waitable timer and a stop event, waited on together.</summary>
internal sealed unsafe class StoppableTimer : IDisposable
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private const long HundredNanosecondsPerSecond = 10_000_000;

    private readonly long _frequency = Stopwatch.Frequency;
    private HANDLE _stopEvent;
    private HANDLE _timer;

    private StoppableTimer(HANDLE stopEvent, HANDLE timer)
    {
        _stopEvent = stopEvent;
        _timer = timer;
    }

    /// <summary>Creates both handles; null, with whichever was made closed again, when either couldn't be made.</summary>
    public static StoppableTimer? TryCreate()
    {
        var stopEvent = PInvoke.CreateEvent(null, true, false, default(PCWSTR));
        var timer = PInvoke.CreateWaitableTimerEx(
            null,
            default(PCWSTR),
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)(SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_MODIFY_STATE | SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE));
        var created = new StoppableTimer(stopEvent, timer);
        if (stopEvent.IsNull || timer.IsNull)
        {
            created.Dispose();
            return null;
        }

        return created;
    }

    /// <summary>Sets the stop event: a wait in progress, and every later one, returns at once.</summary>
    public void Stop() => PInvoke.SetEvent(_stopEvent);

    /// <summary>Sleeps until a timestamp; false when the stop event ended the wait.</summary>
    public bool WaitUntil(long deadline)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return true;
        }

        // A negative due time is relative, in 100 ns.
        var due = -Math.Max(1, (long)((Int128)remaining * HundredNanosecondsPerSecond / _frequency));
        var handles = stackalloc HANDLE[2] { _stopEvent, _timer };
        var woke = PInvoke.SetWaitableTimer(_timer, &due, 0, null, null, false)
            ? PInvoke.WaitForMultipleObjects(2, handles, false, Infinite)
            : PInvoke.WaitForMultipleObjects(1, handles, false, (uint)Math.Max(1, remaining * 1000 / _frequency));
        return woke != (WAIT_EVENT)WaitObject0;
    }

    /// <summary>Waits on the stop event alone; true when it was set within the time.</summary>
    public bool WaitForStop(uint milliseconds)
    {
        var stop = _stopEvent;
        return PInvoke.WaitForMultipleObjects(1, &stop, false, milliseconds) == (WAIT_EVENT)WaitObject0;
    }

    public void Dispose()
    {
        if (!_timer.IsNull)
        {
            PInvoke.CloseHandle(_timer);
            _timer = default;
        }

        if (!_stopEvent.IsNull)
        {
            PInvoke.CloseHandle(_stopEvent);
            _stopEvent = default;
        }
    }
}
