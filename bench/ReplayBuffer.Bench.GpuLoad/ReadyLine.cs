using System.Buffers;
using System.Text;
using System.Text.Json;

namespace ReplayBuffer.Bench.GpuLoad;

/// <summary>The load's exit codes.</summary>
internal static class LoadExit
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    /// <summary>Only a software adapter (WARP, the Basic Render Driver): there is no GPU to load.</summary>
    public const int NoHardwareGpu = 3;

    /// <summary>The GPU was removed or reset under the load.</summary>
    public const int DeviceRemoved = 4;
}

/// <summary>Only a software adapter is present.</summary>
internal sealed class NoHardwareGpuException(string message) : Exception(message);

/// <summary>
/// The one line the load prints once calibrated: <c>ready</c>, a space, and a JSON object with the
/// window, its size, the target and calibrated present rates, the frozen iteration count and the adapter.
/// Bench's <c>GpuLoadProcess</c> parses it.
/// </summary>
internal static class ReadyLine
{
    public const string Prefix = "ready ";

    public static string Format(nint hwnd, int width, int height, double targetFps, double calibratedFps, uint iterations, string adapter, bool tearing, LoadMode mode = LoadMode.Borderless, bool fullscreen = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("hwnd", "0x" + hwnd.ToString("X"));
            json.WriteNumber("width", width);
            json.WriteNumber("height", height);
            json.WriteNumber("targetFps", targetFps);
            json.WriteNumber("calibratedFps", Math.Round(calibratedFps, 2));
            json.WriteNumber("iterations", iterations);
            json.WriteString("adapter", adapter);
            json.WriteBoolean("tearing", tearing);
            json.WriteString("mode", mode.ToString().ToLowerInvariant());
            json.WriteBoolean("fullscreen", fullscreen);
            json.WriteNumber("pid", Environment.ProcessId);
            json.WriteEndObject();
        }

        return Prefix + Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
