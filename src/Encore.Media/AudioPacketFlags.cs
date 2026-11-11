namespace Encore.Media;

/// <summary>Per-packet flags, mirroring WASAPI's <c>AUDCLNT_BUFFERFLAGS_*</c>.</summary>
[Flags]
public enum AudioPacketFlags
{
    None = 0,

    /// <summary>The packet follows a gap in the device's stream.</summary>
    DataDiscontinuity = 1,

    /// <summary>The packet is silence; its samples are not read.</summary>
    Silent = 2,

    /// <summary>The device could not stamp the packet.</summary>
    TimestampError = 4,
}
