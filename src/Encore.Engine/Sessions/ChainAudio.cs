using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Engine.Audio;
using Encore.Media;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

/// <summary>The audio side of an encoder chain: a tap on each device, and each Track's mixer and AAC encoder.</summary>
internal sealed class ChainAudio : IDisposable
{
    private readonly SessionAudio _layout;
    private readonly IAudioDeviceHub _hub;
    private readonly MicMuteSchedule _micMute;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Action<Exception> _fail;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly List<SessionAudioTrack> _tracks = []; // one per device
    private readonly List<AacEncoder> _encoders = []; // one per Track
    private readonly List<TrackMixer> _mixers = []; // one per Track
    private readonly List<AudioTimeline> _timelines = [];
    private readonly SilenceClock _silence;
    private long _sessionAnchorQpc;
    private long _anchorQpc;

    /// <summary>The layout is the spec's devices and Tracks; fail is called with an encode failure.</summary>
    public ChainAudio(
        SessionAudio layout,
        IAudioDeviceHub hub,
        MicMuteSchedule micMute,
        ILoggerFactory loggers,
        ILogger log,
        Action<Exception> fail)
    {
        _layout = layout;
        _hub = hub;
        _micMute = micMute;
        _loggers = loggers;
        _log = log;
        _fail = fail;
        _silence = new SilenceClock(_mixers, FramesAt, fail);
    }

    public IReadOnlyList<SessionAudioTrack> Tracks => _tracks;

    public IReadOnlyList<AacEncoder> Encoders => _encoders;

    public IReadOnlyList<AudioTimeline> Timelines => _timelines;

    /// <summary>The audio anchor as frames since the session anchor: its first AAC sample's PTS.</summary>
    public long StartSample { get; private set; }

    /// <summary>Whether <see cref="Open"/> made an encoder for every Track.</summary>
    public bool IsOpen => _encoders.Count == _layout.Tracks.Count;

    /// <summary>Subscribes a tap to each of the spec's devices; each holds its last second of blocks until the audio starts.</summary>
    public void Subscribe()
    {
        foreach (var device in _layout.Devices)
        {
            var track = new SessionAudioTrack(device);
            try
            {
                track.Subscription = _hub.Subscribe(device, track);
            }
            catch (ErrorCodeException e)
            {
                throw new EngineFailure(e.Code, e.Message);
            }

            _tracks.Add(track);
        }
    }

    /// <summary>Makes each Track's AAC encoder and mixer, writing into the chain's output.</summary>
    public void Open(PacketSink packets, long sessionAnchorQpc)
    {
        _sessionAnchorQpc = sessionAnchorQpc;
        var tracks = _layout.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            var encoder = new AacEncoder(packets, tracks[i].Name, isDefault: i == 0, _loggers.CreateLogger<AacEncoder>());
            _encoders.Add(encoder);
            _mixers.Add(new TrackMixer(tracks[i].DeviceIndexes.Count, encoder));
        }
    }

    /// <summary>Starts the audio at a QPC time: each device takes blocks from here, and mic mute positions count from it.</summary>
    public void Start(long anchorQpc)
    {
        _anchorQpc = anchorQpc;
        StartSample = anchorQpc <= _sessionAnchorQpc
            ? 0
            : (long)((Int128)(anchorQpc - _sessionAnchorQpc) * AudioTimeline.SampleRate / _qpcFrequency);
        if (StartSample > 0)
        {
            foreach (var encoder in _encoders)
            {
                encoder.StartAt(StartSample);
            }
        }

        var tracks = _layout.Tracks;
        var inputs = tracks.Select(t => t.DeviceIndexes).ToList();
        for (var d = 0; d < _tracks.Count; d++)
        {
            var sink = TrackWiring.DeviceSink(d, _tracks[d].Device.IsMic, tracks, inputs, _mixers, _micMute, _qpcFrequency, anchorQpc);
            var timeline = new AudioTimeline(_qpcFrequency, anchorQpc, sink);
            _timelines.Add(timeline);
            _tracks[d].Publish(timeline);
        }

        _silence.Start();
    }

    /// <summary>Unsubscribes every tap; once a subscription is disposed, its track gets no further block.</summary>
    public void StopTaps()
    {
        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            _tracks[i].Subscription?.Dispose();
        }
    }

    /// <summary>Stops the silence clock and waits for a tick in progress; no tick runs after it returns.</summary>
    public void StopSilenceClock() => _silence.Stop();

    /// <summary>Logs each tap's device fault, and fails the session when the encoder or muxer behind one broke.</summary>
    public void ReportFaults()
    {
        foreach (var track in _tracks)
        {
            var name = track.Device.FriendlyName;
            if (track.Subscription?.SubscriberFault is ErrorCodeException
                {
                    Code: ErrorCodes.EncodeFailed or ErrorCodes.DiskFull or ErrorCodes.AccessDenied
                } encodeFault)
            {
                // The encoder or muxer broke, not the device: the file is damaged.
                _fail(encodeFault);
            }
            else if (track.Subscription?.SubscriberFault is { } trackFault)
            {
                _log.LogWarning("{Code}: the session's tap on '{Name}' stopped taking audio: {Message}",
                    ErrorCodes.AudioDeviceFailed, name, trackFault.Message);
            }

            if (track.Subscription?.DeviceFault is { } fault)
            {
                _log.LogWarning("{Code}: '{Name}' failed during capture and was held silent: {Message}",
                    ErrorCodes.AudioDeviceFailed, name, fault.Message);
            }
        }
    }

    /// <summary>Frames since the audio anchor at <paramref name="qpc"/>; 0 before it.</summary>
    public long FramesAt(long qpc) =>
        qpc <= _anchorQpc ? 0 : (long)((Int128)(qpc - _anchorQpc) * AudioTimeline.SampleRate / _qpcFrequency);

    /// <summary>Pads every Track with silence to one end frame, no earlier than the one given, then drains the encoders.</summary>
    public void PadAndFlush(long end)
    {
        foreach (var mixer in _mixers)
        {
            end = Math.Max(end, mixer.Inputs == 0 ? mixer.EmittedFrames : mixer.LongestInputFrames);
        }

        foreach (var mixer in _mixers)
        {
            mixer.PadTo(end);
        }

        foreach (var encoder in _encoders)
        {
            encoder.Flush();
        }
    }

    /// <summary>Logs what each Track wrote and how much of it the limiter held back.</summary>
    public void LogWritten() =>
        _log.LogInformation("Audio written: {Frames} frames per Track; limited: {Limited} frames per Track",
            string.Join(", ", _encoders.Select(a => a.SamplesWritten)), string.Join(", ", _mixers.Select(m => m.LimitedFrames)));

    /// <summary>Unsubscribes, stops the silence clock, then frees the encoders both of them write into.</summary>
    public void Dispose()
    {
        // The device threads write into the encoders: unsubscribe before anything they touch is freed.
        StopTaps();

        // The silence clock writes into the encoders too.
        StopSilenceClock();
        for (var i = _encoders.Count - 1; i >= 0; i--)
        {
            _encoders[i].Dispose();
        }
    }
}
