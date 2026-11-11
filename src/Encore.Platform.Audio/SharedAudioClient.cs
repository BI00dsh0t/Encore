using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace Encore.Platform.Audio;

/// <summary>The steps every WASAPI shared-mode client here takes, from activating it to releasing what it made.</summary>
internal static unsafe class SharedAudioClient
{
    /// <summary>The stream flags every client sets: the engine converts to and from the device's own format.</summary>
    public const uint ConvertingFlags = PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

    /// <summary>An event-driven client's flags: the converting ones and the event callback.</summary>
    public const uint EventDrivenFlags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | ConvertingFlags;

    /// <summary>Activates an <c>IAudioClient</c> on the device; the caller releases it.</summary>
    public static IAudioClient* Activate(IMMDevice* device)
    {
        var clientIid = IAudioClient.IID_Guid;
        IAudioClient* client = null;
        device->Activate(&clientIid, CLSCTX.CLSCTX_ALL, null, (void**)&client);
        return client;
    }

    /// <summary>Initialises the client in shared mode for 32-bit float PCM; the buffer duration is in 100 ns.</summary>
    public static void InitializeShared(IAudioClient* client, uint flags, long bufferDuration, int channels, int sampleRate)
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 32,
            nBlockAlign = (ushort)(channels * sizeof(float)),
            nAvgBytesPerSec = (uint)(sampleRate * channels * sizeof(float)),
            cbSize = 0,
        };
        client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, bufferDuration, 0, &format, null);
    }

    /// <summary>Creates an auto-reset event in the owner's field, then sets it on the client; the owner closes it.</summary>
    public static void AttachEvent(IAudioClient* client, ref HANDLE wake)
    {
        wake = PInvoke.CreateEvent(null, false, false, default(PCWSTR));
        if (wake.IsNull)
        {
            throw new InvalidOperationException("CreateEvent failed.");
        }

        client->SetEventHandle(wake);
    }

    /// <summary>One of the client's services, which the caller releases.</summary>
    public static void* GetService(IAudioClient* client, Guid iid)
    {
        void* service = null;
        client->GetService(&iid, &service);
        return service;
    }

    /// <summary>Joins MMCSS "Pro Audio" for the calling thread; an invalid handle means Windows refused.</summary>
    public static AvRevertMmThreadCharacteristicsSafeHandle JoinProAudio()
    {
        uint taskIndex = 0;
        return PInvoke.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
    }

    /// <summary>Releases a COM pointer held in a field and clears the field.</summary>
    public static void Release<T>(ref T* pointer)
        where T : unmanaged
    {
        if (pointer is not null)
        {
            ((IUnknown*)pointer)->Release();
            pointer = null;
        }
    }

    /// <summary>Closes an event held in a field and clears the field.</summary>
    public static void CloseEvent(ref HANDLE wake)
    {
        if (!wake.IsNull)
        {
            PInvoke.CloseHandle(wake);
            wake = default;
        }
    }
}
