using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Core.Audio;
using Encore.Core.Output;
using Encore.Core.Sessions;
using Encore.Media;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

/// <summary>One capture session: capture, scaler, encoders and Tracks, into a file or Rewind's ring. docs/code-notes.md.</summary>
internal sealed partial class CaptureSession : ICaptureSession
{
    /// <summary>One AAC frame (1024 samples at 48 kHz) in µs, rounded up.</summary>
    private const long AacFrameUs = ((1024 * 1_000_000L) + AudioTimeline.SampleRate - 1) / AudioTimeline.SampleRate;

    /// <summary>How long a save waits for every stream to pass the press before it cuts.</summary>
    private static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(1);

    /// <summary>With no first frame by then, the session fails with <c>capture-failed</c>.</summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often a session that can recover checks whether its GPU device was removed or reset.</summary>
    private static readonly TimeSpan DeviceWatchInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long <see cref="End"/> waits for a running GPU device recovery attempt.</summary>
    private static readonly TimeSpan RecoveryJoinWait = TimeSpan.FromSeconds(10);

    private SessionSpec _spec;
    private readonly ISessionEvents _events;
    private readonly CaptureSessionOptions _options;
    private readonly ICaptureFaults _faults;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly Lock _gate = new();
    private readonly IAudioDeviceHub _audio;
    private readonly MicMuteSchedule _micMute;
    private readonly CaptureStatsCollector? _stats;
    private readonly Timer _firstFrameTimer;

    private WindowInfo? _window;
    private MonitorInfo? _monitor;
    private IReadOnlyList<MonitorPlacement>? _composite;

    // The game's captured size at the anchor, then the size Rewind last moved to; for chains that match the source.
    private VideoSize? _sourceSize;

    // The held size change: the watch runs on the capture thread under the gate; a move's state is under the chain lock.
    private readonly SourceSizeWatch _sizeWatch = new(Stopwatch.Frequency);
    private VideoSize? _movingTo;
    private bool _armMove;
    private bool _armPending;
    private volatile bool _buildAhead = true;

    // A Record press reserves the current chain until its writer attaches, so no size move starts in between.
    private long _recordReservedUntil;
    private static readonly TimeSpan RecordReserve = TimeSpan.FromSeconds(10);
    private string? _path;
    private D3D11Device? _device;
    private ICaptureSource? _capture;
    private volatile bool _outputFinished;

    // The exit drain, orchestrator thread only: the teardown keeps the ring for saves until Release.
    private bool _keepRing;
    private bool _released;

    // Rewind's last Reset in µs since the anchor: the end of the last Clip. It only moves forward.
    private long _resetUs = long.MinValue;

    // How far this session's Clips have claimed the ring, so two concurrent Clips never overlap.
    private readonly ClipClaim _claim = new();

    // The window SetBufferWindow asked for, applied at the next Reset; 0 for none.
    private volatile int _pendingWindow;

    // The encoder chains. _chainLock guards them and is only ever taken after _gate. See docs/code-notes.md.
    private readonly Lock _chainLock = new();
    private readonly ChainWorkers _workers;
    private EncoderChain? _current;
    private EncoderChain? _armed;
    private long _armedQpc;
    private EncoderChain? _retiring;
    private EncoderChain? _prepared;
    private SessionSpec? _wanted;
    private bool _building;

    // Chains being torn down after a newer spec replaced them; a build waits for them, to stay within two chains.
    private int _tearingDown;
    private volatile bool _chainsClosed;

    // A forced restart, under the chain lock: whether one runs, and its thread, which End joins.
    private bool _restarting;
    private Thread? _restartThread;

    // A GPU device loss, under the chain lock; _deviceLost is also written under the gate for the capture thread.
    private bool _deviceLost;
    private bool _recovering;

    // A sleep, from Suspend until the resume's RecoverDevice; an attempt still running then installs nothing.
    private bool _suspended;
    private bool _tornDown;
    private RecordingTap? _lostTap;
    private SessionSpec? _rebuildSpec;
    private Thread? _recoveryThread;

    // Checks the device from the anchor on; under the gate.
    private Timer? _deviceWatch;

    private readonly EncoderResolver _encoderResolver;
    private readonly CaptureSources _sources;
    private long _anchorQpc;
    private long _lastFrameQpc;
    private bool _anchored;
    private bool _stopped;
    private string _stopReason = "";
    private Exception? _failure;

    public CaptureSession(
        SessionSpec spec,
        ISessionEvents events,
        CaptureSessionOptions options,
        IAudioDeviceHub audio,
        IVideoEncoderCatalog encoders,
        ILoggerFactory loggers,
        ICaptureFaults? faults = null)
    {
        _spec = spec;
        _audio = audio;
        _events = events;
        _options = options;
        _faults = faults ?? NoCaptureFaults.Instance;
        _loggers = loggers;
        _log = loggers.CreateLogger<CaptureSession>();
        _encoderResolver = new EncoderResolver(options.Encoder, encoders, _log);
        _workers = new ChainWorkers(_log);
        _sources = new CaptureSources(options, _faults, loggers, _log, OnFrame, OnClosed, Fail);
        _stats = options.Stats;
        _micMute = new MicMuteSchedule(spec.MicMuted);
        _firstFrameTimer = new Timer(_ => OnFirstFrameTimeout(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether this is the Monitor session, not a game window's.</summary>
    private bool IsMonitor => _spec.Kind == SessionKind.Monitor;

    /// <inheritdoc/>
    public string? MonitorDevice => _monitor is { DeviceName.Length: > 0 } monitor ? monitor.DeviceName : null;

    /// <summary>The screens still composited, else <see cref="MonitorDevice"/> alone.</summary>
    public IReadOnlyList<string> MonitorDevices
    {
        get
        {
            ICaptureSource? capture;
            lock (_gate)
            {
                capture = _capture;
            }

            return capture is CompositeCapture composite ? composite.LiveDevices
                : _composite is not null ? [.. _composite.Select(p => p.Monitor.DeviceName)]
                : MonitorDevice is { } device ? [device] : [];
        }
    }

    /// <summary>Whether it holds a Rewind: Replay mode, and not the Monitor session.</summary>
    private bool HasRing => _options.Replay && !IsMonitor;

    public void SetMicMuted(bool muted, long qpc)
    {
        _micMute.Set(muted, qpc);

        // Read without the gate, which a first frame opening the encoders may hold: never block here.
        var frame = Volatile.Read(ref _anchored) ? MicMuteGate.FrameFrom(qpc, Volatile.Read(ref _anchorQpc), _qpcFrequency) : 0;
        _log.LogInformation("Mic {State} from frame {Frame}", muted ? "muted" : "unmuted", frame);
    }

    /// <summary>Records the first failure and stops; the orchestrator is told and ends the session.</summary>
    private void Fail(Exception e)
    {
        if (TakeDeviceLoss(e))
        {
            return;
        }

        lock (_gate)
        {
            _failure ??= e;
            RequestStop("failure: " + e.Message);
        }

        _events.Failed(e);
    }

    /// <summary>Stops taking frames; the first reason is kept. Called under the gate.</summary>
    private void RequestStop(string reason)
    {
        if (_stopped)
        {
            return;
        }

        _stopReason = reason;
        _stopped = true;
    }

    /// <summary>Microseconds since the anchor at <paramref name="qpc"/>.</summary>
    private long MicrosAt(long qpc) => (long)((Int128)(qpc - _anchorQpc) * 1_000_000 / _qpcFrequency);
}
