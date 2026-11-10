using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Media;

namespace Encore.Platform.Audio;

/// <summary>One client on one endpoint, writing packets into a stream on its own thread; disposing it joins that thread.</summary>
internal interface IDeviceClient : IDisposable
{
    AudioEndpoint Endpoint { get; }

    /// <summary>The first failure while capturing, if any; the stream was held silent after it.</summary>
    Exception? Fault { get; }

    /// <summary>Whether the caller is this client's capture thread.</summary>
    bool OnCaptureThread { get; }

    /// <summary>Starts capture: each packet's stamp goes to stamps, then the packet to stream.</summary>
    void Start(DeviceStream stream, AudioStampStats stamps);
}

/// <summary>Opens a client; invalidated is called at most once, from the client's thread, and must only set a flag.</summary>
internal delegate IDeviceClient DeviceClientFactory(string endpointId, AudioFlow flow, Action invalidated);

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
