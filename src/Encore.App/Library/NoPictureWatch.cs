using System.Windows.Threading;

namespace Encore.App.Library;

/// <summary>Notices a file that opens but never shows a picture, such as a cut-off one. UI thread, except FrameArrived.</summary>
internal sealed class NoPictureWatch
{
    private readonly DispatcherTimer _timer;
    private readonly Action _noPicture;

    // Frames the player has delivered since the page opened.
    private int _frames;

    // Whether the wait is on: started when the media is there, ended by the timeout or by Cancel.
    private bool _waiting;

    // Whether the display can't show a picture now (a lock screen, a display change), so the wait doesn't count.
    private bool _paused;

    /// <summary>Calls <paramref name="noPicture"/> on the UI thread when the wait ends with no frame.</summary>
    public NoPictureWatch(Dispatcher dispatcher, Action noPicture)
    {
        _noPicture = noPicture;
        _timer = new DispatcherTimer(Timeout, DispatcherPriority.Background, OnTimeout, dispatcher) { IsEnabled = false };
    }

    /// <summary>How long a page waits for its first picture before saying the file can't be played; tests shorten it.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Starts the wait again from the full timeout; it runs once the display can show a picture.</summary>
    public void Start()
    {
        _timer.Stop();
        _timer.Interval = Timeout;
        _waiting = true;
        if (!_paused)
        {
            _timer.Start();
        }
    }

    /// <summary>Ends the wait without a verdict, as when the page goes away.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _waiting = false;
    }

    /// <summary>Forgets the frames counted so far, for a newly opened page.</summary>
    public void ForgetFrames() => Interlocked.Exchange(ref _frames, 0);

    /// <summary>The player delivered a frame. Any thread.</summary>
    public void FrameArrived() => Interlocked.Increment(ref _frames);

    /// <summary>The display can't show a picture: the wait stops counting.</summary>
    public void Pause()
    {
        _paused = true;
        _timer.Stop();
    }

    /// <summary>The display is back: a wait that has seen no frame starts again from the full timeout.</summary>
    public void Resume()
    {
        _paused = false;
        if (_waiting && Volatile.Read(ref _frames) == 0)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    private void OnTimeout(object? sender, EventArgs e)
    {
        Cancel();
        if (Volatile.Read(ref _frames) == 0)
        {
            _noPicture();
        }
    }
}
