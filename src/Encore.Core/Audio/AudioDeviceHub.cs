using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Audio;

/// <summary>The engine's one internal audio format (AD-13): 48 kHz, stereo, 32-bit float, interleaved.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
}

/// <summary>
/// One block of a device's stream. Frame k of the stream is at <c>origin + k / 48 000 s</c> in QPC,
/// and <see cref="StartQpc"/> is the QPC of this block's first frame. Blocks are contiguous: each
/// starts where the previous one ended. A silent block carries no samples.
/// </summary>
public readonly ref struct AudioBlock
{
    public AudioBlock(long startQpc, int frames, ReadOnlySpan<float> interleaved)
    {
        StartQpc = startQpc;
        Frames = frames;
        Interleaved = interleaved;
    }

    /// <summary>The QPC of the block's first frame.</summary>
    public long StartQpc { get; }

    public int Frames { get; }

    /// <summary><see cref="Frames"/> × 2 samples, or empty for a silent block.</summary>
    public ReadOnlySpan<float> Interleaved { get; }

    public bool IsSilent => Interleaved.IsEmpty;
}

/// <summary>
/// Receives a device's blocks. Calls come on the device's MMCSS thread, one at a time and in
/// stream order; they must not block. A throw cuts this subscriber off (its subscription keeps the
/// exception as <see cref="IAudioSubscription.SubscriberFault"/>), and the others keep receiving.
/// </summary>
public interface IAudioPcmSubscriber
{
    void OnPcm(in AudioBlock block);
}

/// <summary>One subscriber's tap on one device. After <see cref="IDisposable.Dispose"/> returns, its subscriber gets no further call.</summary>
public interface IAudioSubscription : IDisposable
{
    /// <summary>The device's first failure while capturing, if any; its stream was held silent after it.</summary>
    Exception? DeviceFault { get; }

    /// <summary>What the subscriber threw, if it did; it got no call after that.</summary>
    Exception? SubscriberFault { get; }
}

/// <summary>
/// The engine's audio devices (AD-20): at most one client per endpoint, opened by its first
/// subscriber and closed by its last. Each device's stream is QPC-locked 48 kHz stereo float (AD-13).
/// </summary>
public interface IAudioDeviceHub
{
    /// <summary>
    /// Subscribes to an endpoint: loopback for an Output Device, capture for an Input Device.
    /// Throws an <c>audio-device-failed</c> error when the device can't be opened.
    /// </summary>
    IAudioSubscription Subscribe(string endpointId, AudioFlow flow, IAudioPcmSubscriber subscriber);

    /// <summary>
    /// Subscribes to a session's device. A hub may open it by its saved names when its saved id is
    /// gone (a mixer restart that renumbered it, story 8.6); by default it is <see cref="Subscribe(string, AudioFlow, IAudioPcmSubscriber)"/>.
    /// </summary>
    IAudioSubscription Subscribe(Sessions.SessionAudioDevice device, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Subscribe(device.EndpointId, device.Flow, subscriber);
    }
}

/// <summary>
/// The PC's audio endpoints (R1): the present ones of both flows, and a signal when they change.
/// </summary>
public interface IAudioEndpointSource
{
    /// <summary>
    /// Every active Output and Input endpoint, each <see cref="AudioDevice.Available"/>, with each
    /// flow's Windows default (console role) marked <see cref="AudioDevice.IsDefault"/>.
    /// </summary>
    /// <exception cref="Exception">The platform couldn't list them; callers log it and keep their last list.</exception>
    IReadOnlyList<AudioDevice> ListPresent();

    /// <summary>
    /// An endpoint was added, removed, enabled, disabled or renamed, a flow's default (console role)
    /// changed, or the default communications render endpoint changed. Raised on any thread, coalesced
    /// (a burst raises once); handlers must not block. Carries nothing: re-list with <see cref="ListPresent"/>.
    /// </summary>
    event Action? EndpointsChanged;
}

/// <summary>An open audio device the hub dropped or resumed: its endpoint, flow and names.</summary>
public sealed record AudioDeviceDropout(string EndpointId, AudioFlow Flow, string FriendlyName, string ParentName);

/// <summary>
/// The hub's dropout signals (R5). <see cref="Dropped"/>: an open device vanished, and its stream
/// is held silent. <see cref="Resumed"/>: it is bound again, carrying the endpoint it is now bound to.
/// Raised on the hub's reconcile thread; handlers must not block long.
/// </summary>
public interface IAudioDropoutSource
{
    event Action<AudioDeviceDropout>? Dropped;

    event Action<AudioDeviceDropout>? Resumed;
}
