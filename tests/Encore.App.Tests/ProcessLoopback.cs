using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Encore.App.Tests;

/// <summary>
/// Captures this process's own audio, 48 kHz float, mixed to mono, with Windows' process-loopback capture
/// (Windows 10 2004 and later), and finds which of a few tones is loudest at a moment, by Goertzel. It's the
/// in-app player research's check (story 14.4's Heavy test): the player plays at an inaudible volume, and
/// this hears which Track it plays. Activation runs on an MTA thread, as process loopback needs.
/// </summary>
internal sealed class ProcessLoopback : IDisposable
{
    public const int Rate = 48000;

    private readonly List<float> _mono = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private volatile bool _run = true;
    private TimeSpan _started;

    private ProcessLoopback(IAudioCaptureClient capture, IAudioClient client)
    {
        client.Start();
        _started = _clock.Elapsed;
        _thread = new Thread(() =>
        {
            var buffer = new float[Rate];
            while (_run)
            {
                while (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) == 0 && frames > 0)
                {
                    var count = (int)frames * 2;
                    if (buffer.Length < count)
                    {
                        buffer = new float[count];
                    }

                    Marshal.Copy(data, buffer, 0, count);
                    lock (_mono)
                    {
                        for (var i = 0; i < frames; i++)
                        {
                            _mono.Add((flags & 2) != 0 ? 0 : (buffer[2 * i] + buffer[(2 * i) + 1]) / 2);
                        }
                    }

                    capture.ReleaseBuffer(frames);
                }

                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>The time since capture started, to mark moments with.</summary>
    public TimeSpan Now => _clock.Elapsed - _started;

    /// <summary>Starts capturing this process's audio.</summary>
    public static ProcessLoopback Start() => Task.Run(StartAsync).GetAwaiter().GetResult();

    /// <summary>Which of <paramref name="tones"/> (Hz) is loudest in the 20 ms from <paramref name="at"/>; -1 when silent.</summary>
    public int Loudest(TimeSpan at, IReadOnlyList<double> tones)
    {
        float[] samples;
        lock (_mono)
        {
            samples = [.. _mono];
        }

        var start = (int)(at.TotalSeconds * Rate);
        var length = Rate / 50;
        var powers = tones.Select(f => Power(samples, start, length, f)).ToList();
        return powers.Sum() < 1e-12 ? -1 : powers.IndexOf(powers.Max());
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join();
    }

    private static async Task<ProcessLoopback> StartAsync()
    {
        var parameters = new ActivationParams { Type = 1, ProcessId = (uint)Environment.ProcessId, Mode = 0 };
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>());
        try
        {
            Marshal.StructureToPtr(parameters, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { Type = 65, Size = (uint)Marshal.SizeOf<ActivationParams>(), Data = paramsPtr }, blobPtr, false);
            var handler = new Completion();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, blobPtr, handler, out _);
            var client = (IAudioClient)await handler.Done.Task;
            Marshal.StructureToPtr(new WaveFormat { Tag = 3, Channels = 2, Rate = Rate, BytesPerSecond = Rate * 8, BlockAlign = 8, Bits = 32 }, formatPtr, false);
            client.Initialize(0, 0x00020000, 2_000_000, 0, formatPtr, IntPtr.Zero);
            client.GetService(typeof(IAudioCaptureClient).GUID, out var service);
            return new ProcessLoopback((IAudioCaptureClient)service, client);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static double Power(float[] x, int start, int length, double frequency)
    {
        double w = 2 * Math.PI * frequency / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (var i = Math.Max(0, start); i < start + length && i < x.Length; i++)
        {
            var s0 = x[i] + (c * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        return (s1 * s1) + (s2 * s2) - (c * s1 * s2);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr parameters, IActivateCompletion handler, out IActivateOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int Type;
        public uint ProcessId;
        public int Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public uint Padding;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag;
        public ushort Channels;
        public uint Rate;
        public uint BytesPerSecond;
        public ushort BlockAlign;
        public ushort Bits;
        public ushort Extra;
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateOperation
    {
        void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateCompletion
    {
        void ActivateCompleted(IActivateOperation operation);
    }

    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject;

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);

        void GetBufferSize(out uint frames);

        void GetStreamLatency(out long latency);

        void GetCurrentPadding(out uint padding);

        void IsFormatSupported();

        void GetMixFormat();

        void GetDevicePeriod();

        void Start();

        void Stop();

        void Reset();

        void SetEventHandle(IntPtr handle);

        void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);

        void ReleaseBuffer(uint frames);

        void GetNextPacketSize(out uint frames);
    }

    [ComVisible(true)]
    internal sealed class Completion : IActivateCompletion, IAgileObject
    {
        public TaskCompletionSource<object> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateOperation operation)
        {
            operation.GetActivateResult(out var result, out var activated);
            if (result < 0)
            {
                Done.SetException(Marshal.GetExceptionForHR(result)!);
            }
            else
            {
                Done.SetResult(activated);
            }
        }
    }
}
