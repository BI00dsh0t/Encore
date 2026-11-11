using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Media;

namespace Encore.Platform.Audio;

/// <summary>Keeps a dropped device's stream continuous by filling it with silence 50 ms behind now, every 20 ms.</summary>
internal sealed class SilenceHold : IDisposable
{
    private const int PeriodMs = 20;
    private const long FillLagMs = 50;

    private readonly DeviceStream _stream;
    private readonly ILogger _log;
    private readonly string _name;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private int _disposed;

    public SilenceHold(DeviceStream stream, string name, ILogger log)
    {
        _stream = stream;
        _name = name;
        _log = log;
        _thread = new Thread(Run) { Name = "Audio silence hold", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Whether the caller is the hold's thread.</summary>
    public bool OnCaptureThread => Thread.CurrentThread == _thread;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        if (!OnCaptureThread)
        {
            _thread.Join();
        }

        _stop.Dispose();
    }

    private void Run()
    {
        var lag = FillLagMs * Stopwatch.Frequency / 1000;
        do
        {
            try
            {
                _stream.FillTo(Stopwatch.GetTimestamp() - lag);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Silence fill for '{Name}' failed; its stream stops here", _name);
                return;
            }
        }
        while (!_stop.Wait(PeriodMs));
    }
}
