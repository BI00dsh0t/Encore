using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Feedback;
using ReplayBuffer.Core.Settings;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Audio;

/// <summary>
/// Warns when a Track would record the Feedback Sounds (AD-15): a <c>feedback-device-captured</c>
/// warning (severity warning, source <c>feedback</c>, no overlay, dismissable) while a Track of the
/// active Track Layout, resolved for the Target Game's profile, else Global, mixes the Output Device
/// the sounds play on. <see cref="Check"/> runs on each settings, endpoint and target change. Each
/// distinct condition raises once; the warning is withdrawn when the condition clears or changes.
/// </summary>
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

    /// <summary>Subscribes to the store's and the endpoints' changes; the owner calls <see cref="Check"/> on a target change and once at start.</summary>
    /// <param name="profileId">The Target Game's profile id, or <c>null</c> with no Target.</param>
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
                $"Feedback Sounds play on '{device}', which {(captured.Count == 1 ? "Track" : "Tracks")} {tracks} {(captured.Count == 1 ? "records" : "record")}. Pick another Feedback Sound device, or take it out of the Track.",
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
