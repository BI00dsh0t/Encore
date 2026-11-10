using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Audio;
using Encore.Core.Feedback;
using Encore.Core.Settings;
using Encore.Engine.Ipc;
using Encore.Engine.Settings;

namespace Encore.Engine.Audio;

/// <summary>Warns while a Track of the active Track Layout records the Output Device that Feedback Sounds play on.</summary>
internal sealed class FeedbackDeviceCheck : IDisposable
{
    private const string Source = "feedback";

    private readonly Lock _lock = new();
    private readonly FeedbackSoundPlayer _sounds;
    private readonly SettingsStore _store;
    private readonly IAudioEndpointSource _endpoints;
    private readonly Func<string?> _profileId;
    private readonly ReplacedWarning _warning;
    private readonly ILogger _log;
    private string? _condition;
    private bool _disposed;

    /// <summary>Subscribes to settings and endpoint changes; the owner calls Check on a target change and at start.</summary>
    public FeedbackDeviceCheck(
        FeedbackSoundPlayer sounds,
        SettingsStore store,
        IAudioEndpointSource endpoints,
        Func<string?> profileId,
        Warnings warnings,
        ILogger log)
    {
        _sounds = sounds;
        _store = store;
        _endpoints = endpoints;
        _profileId = profileId;
        _warning = new ReplacedWarning(warnings, ErrorCodes.FeedbackDeviceCaptured, Source);
        _log = log;
        _store.Changed += OnSettingsChanged;
        _endpoints.EndpointsChanged += Check;
    }

    /// <summary>Re-checks the active layout against the sound endpoint. Never throws.</summary>
    public void Check()
    {
        lock (_lock)
        {
            if (_disposed || !TryReadEndpoint(out var endpoint))
            {
                return;
            }

            var layout = SettingsResolver.Resolve(_store.Current, _profileId()).TrackLayout;
            IReadOnlyList<(Track Track, LayoutDevice Device)> captured =
                endpoint is null ? [] : FeedbackCapture.CapturingTracks(layout, endpoint);
            var condition = captured.Count == 0
                ? null
                : endpoint + "|" + string.Join(",", captured.Select(capture =>
                    $"{capture.Track.Id:N}:{capture.Track.Name}:{capture.Device.FriendlyName}"));
            if (condition == _condition)
            {
                return;
            }

            _condition = condition;
            if (condition is null)
            {
                _warning.Withdraw();
                return;
            }

            var device = captured[0].Device.FriendlyName;
            var tracks = string.Join(", ", captured.Select(capture => $"'{capture.Track.Name}'"));
            _warning.Raise(Message(device, tracks, captured.Count));
            _log.LogWarning("Feedback Sounds play on {Device}, which {Tracks} record", device, tracks);
        }
    }

    /// <summary>Unsubscribes; later checks do nothing, and an active warning stays.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _store.Changed -= OnSettingsChanged;
        _endpoints.EndpointsChanged -= Check;
    }

    private static string Message(string device, string tracks, int count)
    {
        var (noun, verb) = count == 1 ? ("track", "records") : ("tracks", "record");
        return $"Sounds play on '{device}', which the audio {noun} {tracks} {verb}, so they end up in your Clips. "
            + "Pick another sound device on the Sounds page, or remove it from the track.";
    }

    /// <summary>The endpoint sounds play on; false when it couldn't be read, so the check keeps its last result.</summary>
    private bool TryReadEndpoint(out string? endpoint)
    {
        try
        {
            endpoint = _sounds.Endpoint();
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't read the Feedback Sound endpoint; the capture check keeps its last result");
            endpoint = null;
            return false;
        }
    }

    private void OnSettingsChanged(SettingsDocument document) => Check();
}
