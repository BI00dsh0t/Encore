namespace Encore.Core.Audio;

/// <summary>The hub's dropout signals, raised on its reconcile thread: a device vanished, or it is bound again.</summary>
public interface IAudioDropoutSource
{
    event Action<AudioDeviceDropout>? Dropped;

    event Action<AudioDeviceDropout>? Resumed;
}
