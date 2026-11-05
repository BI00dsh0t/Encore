using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Engine.Ipc;

namespace ReplayBuffer.Engine.Audio;

/// <summary>
/// Turns each audio device dropout into one <c>device-dropout</c> warning (R5): severity warning,
/// source <c>audio</c>, no overlay, dismissable. A resume raises nothing and dismisses nothing:
/// warnings go only by <c>warning.dismiss</c> (AD-3). It logs nothing; the hub's entry is the log line.
/// </summary>
internal sealed class DeviceDropoutReporter : IDisposable
{
    private readonly IAudioDropoutSource _source;
    private readonly Warnings _warnings;
    private int _disposed;

    public DeviceDropoutReporter(IAudioDropoutSource source, Warnings warnings)
    {
        _source = source;
        _warnings = warnings;
        _source.Dropped += OnDropped;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Dropped -= OnDropped;
        }
    }

    private void OnDropped(AudioDeviceDropout dropout) =>
        _warnings.Raise(
            ErrorCodes.DeviceDropout,
            WarningSeverity.Warning,
            "audio",
            $"'{dropout.FriendlyName}' was disconnected. Your audio tracks record silence from it until it's back.",
            overlay: false);
}
