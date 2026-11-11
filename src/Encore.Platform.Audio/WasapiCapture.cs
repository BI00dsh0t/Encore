using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Audio;
using Encore.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Encore.Platform.Audio;

/// <summary>The hub's WASAPI client for one endpoint: event-driven capture on its own thread. Details: docs/code-notes.md.</summary>
internal sealed unsafe class WasapiCapture : IDeviceClient
{
    /// <summary><c>AUDCLNT_E_DEVICE_INVALIDATED</c>: the endpoint was removed, disabled or reconfigured.</summary>
    internal const int DeviceInvalidated = unchecked((int)0x88890004);

    private const long BufferDuration100ns = 1_000_000; // 100 ms
    private const uint WaitTimeoutMs = 20;
    private const long IdleFillLagMs = 50;

    private readonly ILogger _logger;
    private readonly Action? _invalidated;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private IAudioClient* _client;
    private IAudioCaptureClient* _capture;
    private HANDLE _event;
    private Thread? _thread;
    private DeviceStream? _stream;
    private AudioStampStats? _stampStats;
    private volatile bool _stopping;

    /// <summary>Opens and initializes the client; capture starts with <see cref="Start"/>.</summary>
    public WasapiCapture(string endpointId, AudioFlow flow, ILogger logger, Action? invalidated = null)
    {
        _logger = logger;
        _invalidated = invalidated;
        Endpoint = new AudioEndpoint(endpointId, endpointId, flow);
        IMMDevice* device = null;
        try
        {
            AudioEndpoints.EnsureCom();
            device = AudioEndpoints.OpenDevice(endpointId);
            Endpoint = AudioEndpoints.Describe(device, flow);
            _client = SharedAudioClient.Activate(device);
            var flags = SharedAudioClient.EventDrivenFlags;
            if (IsLoopback)
            {
                flags |= PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK;
            }

            SharedAudioClient.InitializeShared(_client, flags, BufferDuration100ns, AudioFormat.Channels, AudioFormat.SampleRate);
            SharedAudioClient.AttachEvent(_client, ref _event);
            _capture = (IAudioCaptureClient*)SharedAudioClient.GetService(_client, IAudioCaptureClient.IID_Guid);
        }
        catch (Exception e) when (e is not ErrorCodeException)
        {
            Dispose();
            throw new ErrorCodeException(ErrorCodes.AudioDeviceFailed, $"Could not open '{Endpoint.FriendlyName}': {e.Message}", e);
        }
        finally
        {
            if (device is not null) device->Release();
        }

        logger.LogInformation("Opened {Flow} device '{Name}' ({Mode})", flow, Endpoint.FriendlyName, IsLoopback ? "loopback" : "capture");
    }

    public AudioEndpoint Endpoint { get; }

    /// <summary>The first failure while capturing, if any; the stream was held silent after it.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Whether the caller is this client's capture thread.</summary>
    public bool OnCaptureThread => Thread.CurrentThread == _thread;

    private bool IsLoopback => Endpoint.Flow == AudioFlow.Output;

    /// <summary>Starts capture: each packet's stamp goes to stampStats, then the packet to stream.</summary>
    public void Start(DeviceStream stream, AudioStampStats stampStats)
    {
        _stream = stream;
        _stampStats = stampStats;
        _thread = new Thread(Run) { Name = $"WASAPI {Endpoint.Flow}", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Stops the thread and the client. Every packet read so far is in the stream.</summary>
    public void Stop()
    {
        _stopping = true;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        SharedAudioClient.Release(ref _capture);
        SharedAudioClient.Release(ref _client);
        SharedAudioClient.CloseEvent(ref _event);
    }

    /// <summary>Whether <paramref name="error"/>, or an exception it wraps, carries <c>AUDCLNT_E_DEVICE_INVALIDATED</c>.</summary>
    internal static bool IsInvalidated(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current.HResult == DeviceInvalidated)
            {
                return true;
            }
        }

        return false;
    }

    private void Run()
    {
        AudioEndpoints.EnsureCom();
        var proAudio = SharedAudioClient.JoinProAudio();
        if (proAudio.IsInvalid)
        {
            _logger.LogWarning("MMCSS 'Pro Audio' was refused for '{Name}'", Endpoint.FriendlyName);
        }

        var stream = _stream!;
        var stampStats = _stampStats!;
        var failed = !TryStartClient();
        try
        {
            Pump(stream, stampStats, failed);
        }
        finally
        {
            try
            {
                _client->Stop();
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "IAudioClient.Stop failed");
            }

            proAudio.Dispose();
        }
    }

    /// <summary>Starts the client; false when it couldn't start, and the stream is held silent from the start.</summary>
    private bool TryStartClient()
    {
        try
        {
            _client->Start();
            return true;
        }
        catch (Exception e) when (IsInvalidated(e))
        {
            OnInvalidated(e);
            return false;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not start '{Name}'; holding it silent", Endpoint.FriendlyName);
            Fault = e;
            return false;
        }
    }

    /// <summary>Wakes on each packet event or timeout until stopped: drains packets, or fills silence once failed.</summary>
    private void Pump(DeviceStream stream, AudioStampStats stampStats, bool failed)
    {
        var dead = false;
        while (!_stopping)
        {
            var wait = PInvoke.WaitForSingleObject(_event, WaitTimeoutMs);
            if (dead)
            {
                continue;
            }

            try
            {
                Serve(stream, stampStats, failed, wait == WAIT_EVENT.WAIT_OBJECT_0);
            }
            catch (Exception e) when (!failed && IsInvalidated(e))
            {
                OnInvalidated(e);
                failed = true;
            }
            catch (Exception e) when (!failed)
            {
                _logger.LogError(e, "Capture from '{Name}' failed; holding it silent", Endpoint.FriendlyName);
                Fault = e;
                failed = true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Silence fill for '{Name}' failed; its stream stops here", Endpoint.FriendlyName);
                dead = true;
            }
        }
    }

    private void Serve(DeviceStream stream, AudioStampStats stampStats, bool failed, bool signalled)
    {
        if (!failed && signalled)
        {
            Drain(stream, stampStats);
        }
        else if (IsLoopback || failed)
        {
            // An idle render endpoint and a failed device send no packets: fill with silence to keep the stream continuous.
            stream.FillTo(Stopwatch.GetTimestamp() - (IdleFillLagMs * _qpcFrequency / 1000));
        }
    }

    /// <summary>The device went away under the client: held silent, not a fault; the hub is told once.</summary>
    private void OnInvalidated(Exception error)
    {
        _logger.LogDebug(error, "'{Name}' was invalidated; holding it silent", Endpoint.FriendlyName);
        try
        {
            _invalidated?.Invoke();
        }
        catch (Exception callback)
        {
            _logger.LogError(callback, "The invalidated callback for '{Name}' failed", Endpoint.FriendlyName);
        }
    }

    private void Drain(DeviceStream stream, AudioStampStats stampStats)
    {
        while (true)
        {
            uint next;
            _capture->GetNextPacketSize(&next);
            if (next == 0)
            {
                return;
            }

            byte* data;
            uint frames;
            uint flags;
            ulong qpcPosition;
            _capture->GetBuffer(&data, &frames, &flags, null, &qpcPosition);
            var readQpc = Stopwatch.GetTimestamp();
            try
            {
                var packetFlags = (AudioPacketFlags)(flags & 0x7);
                var stamp = QpcClock.From100ns((long)qpcPosition, _qpcFrequency);
                stampStats.Record(stamp, (int)frames, packetFlags, readQpc);
                var samples = (packetFlags & AudioPacketFlags.Silent) != 0
                    ? ReadOnlySpan<float>.Empty
                    : new ReadOnlySpan<float>(data, (int)frames * AudioFormat.Channels);
                stream.Push(stamp, (int)frames, samples, packetFlags);
            }
            finally
            {
                _capture->ReleaseBuffer(frames);
            }
        }
    }
}
