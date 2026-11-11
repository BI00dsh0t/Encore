using System.Diagnostics;
using Encore.Contracts;
using Encore.Media;

namespace Encore.Engine.Sessions;

/// <summary>Advances the Tracks that have no devices, which are silence, to the current time on a timer.</summary>
internal sealed class SilenceClock(IReadOnlyList<TrackMixer> mixers, Func<long, long> framesAt, Action<Exception> fail)
{
    /// <summary>How often a Track with no devices is advanced to the current QPC.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _stopped;

    /// <summary>Starts the timer when a Track has no devices; a clock that was stopped stays stopped.</summary>
    public void Start()
    {
        if (!mixers.Any(m => m.Inputs == 0))
        {
            return;
        }

        lock (_gate)
        {
            if (!_stopped)
            {
                _timer = new Timer(_ => Advance(), null, Interval, Interval);
            }
        }
    }

    /// <summary>Stops the clock and waits for a tick in progress; no tick runs after it returns.</summary>
    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            _stopped = true;
            timer = _timer;
            _timer = null;
        }

        if (timer is null)
        {
            return;
        }

        using var done = new ManualResetEvent(false);
        if (timer.Dispose(done))
        {
            done.WaitOne();
        }
    }

    /// <summary>The timer's tick: advances every Track with no devices to the current QPC.</summary>
    private void Advance()
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            try
            {
                PadSilentTracks(framesAt(Stopwatch.GetTimestamp()));
            }
            catch (Exception e)
            {
                // The encoder or muxer broke: the clock stops and the session fails.
                _stopped = true;
                failure = e;
            }
        }

        if (failure is not null)
        {
            fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "Audio encode failed: " + failure.Message, failure));
        }
    }

    private void PadSilentTracks(long now)
    {
        foreach (var mixer in mixers)
        {
            if (mixer.Inputs == 0)
            {
                mixer.PadTo(now);
            }
        }
    }
}
