using System.Diagnostics;
using Encore.Contracts;
using Encore.Core.Feedback;
using Windows.Win32.Media.Audio;

namespace Encore.Platform.Audio;

/// <summary>Plays a Feedback Sound over WASAPI shared mode: writes it all, waits for it to drain, then releases the client.</summary>
public sealed unsafe class FeedbackSoundOutput : IFeedbackSoundOutput
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    internal static readonly TimeSpan DrainSlack = TimeSpan.FromMilliseconds(500);

    private const long BufferDuration100ns = 10_000_000; // 1 s
    private const int Channels = FeedbackSoundSynth.Channels;

    /// <inheritdoc/>
    public string? DefaultEndpointId() => AudioEndpoints.DefaultCommunications()?.Id;

    /// <inheritdoc/>
    public void Play(string endpointId, ReadOnlyMemory<float> pcm)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        IMMDevice* device = null;
        IAudioClient* client = null;
        IAudioRenderClient* render = null;
        var started = false;
        try
        {
            AudioEndpoints.EnsureCom();
            device = AudioEndpoints.OpenDevice(endpointId);
            client = SharedAudioClient.Activate(device);
            SharedAudioClient.InitializeShared(
                client, SharedAudioClient.ConvertingFlags, BufferDuration100ns, Channels, FeedbackSoundSynth.SampleRate);

            uint bufferFrames;
            client->GetBufferSize(&bufferFrames);
            render = (IAudioRenderClient*)SharedAudioClient.GetService(client, IAudioRenderClient.IID_Guid);
            var frames = WriteSound(render, pcm, bufferFrames);
            if (frames == 0)
            {
                return;
            }

            client->Start();
            started = true;
            WaitUntilPlayed(client, frames);
        }
        catch (Exception e) when (e is not ErrorCodeException)
        {
            throw new ErrorCodeException(
                ErrorCodes.AudioDeviceFailed, $"Could not play a Feedback Sound on '{endpointId}': {e.Message}", e);
        }
        finally
        {
            if (started)
            {
                TryStop(client);
            }

            if (render is not null) render->Release();
            if (client is not null) client->Release();
            if (device is not null) device->Release();
        }
    }

    /// <summary>Copies the sound into the buffer and returns its frames; 0 for an empty sound, which isn't played.</summary>
    private static uint WriteSound(IAudioRenderClient* render, ReadOnlyMemory<float> pcm, uint bufferFrames)
    {
        // Every sound is shorter than the buffer; a longer one would be cut at its end.
        var frames = (uint)Math.Min(pcm.Length / Channels, bufferFrames);
        if (frames == 0)
        {
            return 0;
        }

        byte* data;
        render->GetBuffer(frames, &data);
        var samples = (int)(frames * Channels);
        pcm.Span[..samples].CopyTo(new Span<float>(data, samples));
        render->ReleaseBuffer(frames, 0);
        return frames;
    }

    /// <summary>Polls until the buffer has drained, or the sound's length and some slack have passed.</summary>
    private static void WaitUntilPlayed(IAudioClient* client, uint frames)
    {
        var deadline = TimeSpan.FromSeconds((double)frames / FeedbackSoundSynth.SampleRate) + DrainSlack;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < deadline)
        {
            uint padding;
            client->GetCurrentPadding(&padding);
            if (padding == 0)
            {
                return;
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static void TryStop(IAudioClient* client)
    {
        try
        {
            client->Stop();
        }
        catch (Exception)
        {
            // The device is already gone.
        }
    }
}
