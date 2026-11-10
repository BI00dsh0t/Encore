namespace Encore.Core.Audio;

/// <summary>Receives a device's blocks on its MMCSS thread, in order; it must not block, and a throw cuts it off.</summary>
public interface IAudioPcmSubscriber
{
    void OnPcm(in AudioBlock block);
}
