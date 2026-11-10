using Encore.Media;

namespace Encore.Platform.Audio;

/// <summary>One client on one endpoint, writing packets into a stream on its own thread; disposing it joins that thread.</summary>
internal interface IDeviceClient : IDisposable
{
    AudioEndpoint Endpoint { get; }

    /// <summary>The first failure while capturing, if any; the stream was held silent after it.</summary>
    Exception? Fault { get; }

    /// <summary>Whether the caller is this client's capture thread.</summary>
    bool OnCaptureThread { get; }

    /// <summary>Starts capture: each packet's stamp goes to stamps, then the packet to stream.</summary>
    void Start(DeviceStream stream, AudioStampStats stamps);
}
