using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Core.Feedback;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace ReplayBuffer.Platform.Audio;

/// <summary>
/// Feedback Sounds over WASAPI shared-mode render: each play opens the endpoint with a 1 s buffer
/// and no event callback, writes the whole sound, starts, polls the padding every
/// <see cref="PollInterval"/> until it has drained (at most the sound's length plus
/// <see cref="DrainSlack"/>), then stops and releases the client.
/// </summary>
public sealed unsafe class FeedbackSoundOutput : IFeedbackSoundOutput
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    internal static readonly TimeSpan DrainSlack = TimeSpan.FromMilliseconds(500);

    private const long BufferDuration100ns = 10_000_000; // 1 s

    /// <inheritdoc/>
    public string? DefaultEndpointId() => AudioEndpoints.DefaultCommunications()?.Id;

    /// <inheritdoc/>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.AudioDeviceFailed"/>: the endpoint can't be opened or failed while playing.</exception>
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

            var clientIid = IAudioClient.IID_Guid;
            device->Activate(&clientIid, CLSCTX.CLSCTX_ALL, null, (void**)&client);

            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = FeedbackSoundSynth.Channels,
                nSamplesPerSec = FeedbackSoundSynth.SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = FeedbackSoundSynth.Channels * sizeof(float),
                nAvgBytesPerSec = FeedbackSoundSynth.SampleRate * FeedbackSoundSynth.Channels * sizeof(float),
                cbSize = 0,
            };
            var flags = PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration100ns, 0, &format, null);

            uint bufferFrames;
            client->GetBufferSize(&bufferFrames);
            var renderIid = IAudioRenderClient.IID_Guid;
            client->GetService(&renderIid, (void**)&render);

            // Every sound is shorter than the buffer; a longer one would be cut at its end.
            var frames = (uint)Math.Min(pcm.Length / FeedbackSoundSynth.Channels, bufferFrames);
            if (frames == 0)
            {
                return;
            }

            byte* data;
            render->GetBuffer(frames, &data);
            pcm.Span[..(int)(frames * FeedbackSoundSynth.Channels)].CopyTo(new Span<float>(data, (int)frames * FeedbackSoundSynth.Channels));
            render->ReleaseBuffer(frames, 0);

            client->Start();
            started = true;
            var deadline = TimeSpan.FromSeconds((double)frames / FeedbackSoundSynth.SampleRate) + DrainSlack;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < deadline)
            {
                uint padding;
                client->GetCurrentPadding(&padding);
                if (padding == 0)
                {
                    break;
                }

                Thread.Sleep(PollInterval);
            }
        }
        catch (Exception e) when (e is not ErrorCodeException)
        {
            throw new ErrorCodeException(ErrorCodes.AudioDeviceFailed, $"Could not play a Feedback Sound on '{endpointId}': {e.Message}", e);
        }
        finally
        {
            if (started)
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

            if (render is not null) render->Release();
            if (client is not null) client->Release();
            if (device is not null) device->Release();
        }
    }
}
