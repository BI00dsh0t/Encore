using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
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
    private readonly Warnings _warnings;
    private readonly ILogger _log;
    private string? _condition;
    private string? _warningId;
    private bool _disposed;

    /// <summary>Subscribes to settings and endpoint changes; the owner calls Check on a target change and at start.</summary>
    public FeedbackDeviceCheck(FeedbackSoundPlayer sounds, SettingsStore store, IAudioEndpointSource endpoints, Func<string?> profileId, Warnings warnings, ILogger log)
    {
        _sounds = sounds;
        _store = store;
        _endpoints = endpoints;
        _profileId = profileId;
        _warnings = warnings;
        _log = log;
        _store.Changed += OnSettingsChanged;
        _endpoints.EndpointsChanged += Check;
    }

    /// <summary>Re-checks the active layout against the sound endpoint. Never throws.</summary>
    public void Check()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            string? endpoint;
            try
            {
                endpoint = _sounds.Endpoint();
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Couldn't read the Feedback Sound endpoint; the capture check keeps its last result");
                return;
            }

            var layout = SettingsResolver.Resolve(_store.Current, _profileId()).TrackLayout;
            IReadOnlyList<(Track Track, LayoutDevice Device)> captured = endpoint is null ? [] : FeedbackCapture.CapturingTracks(layout, endpoint);
            var condition = captured.Count == 0
                ? null
                : endpoint + "|" + string.Join(",", captured.Select(c => $"{c.Track.Id:N}:{c.Track.Name}:{c.Device.FriendlyName}"));
            if (condition == _condition)
            {
                return;
            }

            _condition = condition;
            if (_warningId is { } id)
            {
                _warningId = null;
                _warnings.Withdraw(id);
            }

            if (condition is null)
            {
                return;
            }

            var device = captured[0].Device.FriendlyName;
            var tracks = string.Join(", ", captured.Select(c => $"'{c.Track.Name}'"));
            _warningId = _warnings.Raise(
                ErrorCodes.FeedbackDeviceCaptured,
                WarningSeverity.Warning,
                Source,
                $"Sounds play on '{device}', which the audio {(captured.Count == 1 ? "track" : "tracks")} {tracks} {(captured.Count == 1 ? "records" : "record")}, so they end up in your Clips. Pick another sound device on the Sounds page, or remove it from the track.",
                overlay: false).Id;
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

    private void OnSettingsChanged(SettingsDocument document) => Check();
}
