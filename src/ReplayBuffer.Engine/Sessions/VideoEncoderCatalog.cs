using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Capture;
using ReplayBuffer.Platform.FFmpeg;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>Which encoder a session uses for each codec (R2, AD-14).</summary>
internal interface IVideoEncoderCatalog
{
    /// <summary>
    /// The FFmpeg encoder the probe chose for <paramref name="codec"/>, or null when none works on
    /// this PC. The codec is probed on first use; a result is cached, a throw is not.
    /// </summary>
    /// <exception cref="Exception">The probe itself failed (FFmpeg missing, no D3D11 device); the next call probes again.</exception>
    string? EncoderFor(VideoCodec codec);
}

/// <summary>
/// The encoder probe, run once per codec and cached for the engine's life; a probe that throws is
/// not cached, so a transient failure is retried on the next use. Each codec is probed on its own
/// D3D11 device on adapter 0; choosing the adapter per game is not done.
/// </summary>
internal sealed class VideoEncoderCatalog : IVideoEncoderCatalog
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Func<VideoCodec, string?> _probe;

    // One gate per codec, so a codec is probed by one caller at a time while the others run freely.
    private readonly Dictionary<VideoCodec, Lock> _gates = EncoderProbe.Codecs.ToDictionary(codec => codec, _ => new Lock());
    private readonly Dictionary<VideoCodec, string?> _results = [];

    public VideoEncoderCatalog(ILoggerFactory loggers)
        : this(loggers, null)
    {
    }

    /// <param name="probe">Replaces the real probe of one codec, for tests; null for the real one.</param>
    internal VideoEncoderCatalog(ILoggerFactory loggers, Func<VideoCodec, string?>? probe)
    {
        _loggers = loggers;
        _log = loggers.CreateLogger("EncoderProbe");
        _probe = probe ?? Probe;
    }

    public string? EncoderFor(VideoCodec codec)
    {
        lock (_gates[codec])
        {
            lock (_results)
            {
                if (_results.TryGetValue(codec, out var cached))
                {
                    return cached;
                }
            }

            // A throw leaves nothing cached.
            var encoder = _probe(codec);
            lock (_results)
            {
                _results[codec] = encoder;
            }

            return encoder;
        }
    }

    /// <summary>
    /// Probes every codec and returns the options to publish. A codec whose probe throws (FFmpeg
    /// missing, no device) is logged as a warning and not offered; the others still are.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled between two codecs.</exception>
    public VideoOptions ProbeAll(CancellationToken cancellationToken)
    {
        var offers = new List<CodecOffer>();
        foreach (var codec in EncoderProbe.Codecs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (EncoderFor(codec) is { } encoder)
                {
                    offers.Add(new CodecOffer(codec, encoder, VideoOptions.MayNotPlayEverywhereFor(codec)));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("The {Codec} encoder probe failed, so that codec is not offered: {Message}", codec, e.Message);
            }
        }

        _log.LogInformation("Video options: {Codecs}", offers.Count == 0 ? "no codec" : string.Join(", ", offers.Select(o => $"{o.Codec} via {o.Encoder}")));
        return VideoOptions.FromProbe(offers);
    }

    private string? Probe(VideoCodec codec)
    {
        FFmpegLibrary.Initialize(_loggers.CreateLogger("FFmpeg"));
        using var device = D3D11Device.Create(0, _loggers.CreateLogger<D3D11Device>());
        return EncoderProbe.ProbeCodec(device.NativeDevice, codec, _log)?.Encoder;
    }
}
