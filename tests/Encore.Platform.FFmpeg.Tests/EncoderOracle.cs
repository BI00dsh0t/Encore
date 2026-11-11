using System.Diagnostics;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>The oracle for the probe: which hardware encoders <c>ffmpeg.exe</c> itself can encode one frame with.</summary>
public static class EncoderOracle
{
    /// <summary>The accepted candidates, in <see cref="EncoderProbe.HardwareCandidates"/> order.</summary>
    public static IReadOnlyList<string> Accepted(string ffmpegFolder) =>
        [.. EncoderProbe.HardwareCandidates.Where(name => Accepts(ffmpegFolder, name))];

    public static bool Accepts(string ffmpegFolder, string encoder)
    {
        var start = new ProcessStartInfo(Path.Combine(ffmpegFolder, "ffmpeg.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "nullsrc=s=640x360", "-frames:v", "1", "-c:v", encoder, "-f", "null", "-" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill();
            return false;
        }

        _ = stdout.Result + stderr.Result;
        return process.ExitCode == 0;
    }
}
