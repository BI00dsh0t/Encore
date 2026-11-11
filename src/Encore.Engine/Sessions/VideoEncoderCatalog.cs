using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

/// <summary>The encoder probe, run once per codec and cached for the engine's life; each probes on adapter 0.</summary>
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

    /// <summary>Probes every codec and returns the options to publish; a codec whose probe throws is logged and not offered.</summary>
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

        var codecs = offers.Count == 0 ? "no codec" : string.Join(", ", offers.Select(o => $"{o.Codec} via {o.Encoder}"));
        _log.LogInformation("Video options: {Codecs}", codecs);
        return VideoOptions.FromProbe(offers);
    }

    private string? Probe(VideoCodec codec)
    {
        FFmpegLibrary.Initialize(_loggers.CreateLogger("FFmpeg"));
        using var device = D3D11Device.Create(0, _loggers.CreateLogger<D3D11Device>());
        return EncoderProbe.ProbeCodec(device.NativeDevice, codec, _log)?.Encoder;
    }
}
