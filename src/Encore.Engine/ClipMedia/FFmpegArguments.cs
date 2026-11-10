using System.Globalization;
using Encore.Contracts.Messages;

namespace Encore.Engine.ClipMedia;

/// <summary>The parts of FFmpeg command lines the clip operations share.</summary>
internal static class FFmpegArguments
{
    /// <summary>A number as FFmpeg reads it: invariant, with up to nine decimals.</summary>
    public static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>The path of a tool shipped beside the engine.</summary>
    public static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, name + ".exe");

    /// <summary>The filter that cuts one audio stream to a range at 48 kHz, padded to the range's length.</summary>
    public static string AudioTrim(int streamIndex, ClipRange range, double origin) =>
        $"[0:{streamIndex}]atrim=start={Number(origin + range.Start)}:end={Number(origin + range.End)},"
        + $"asetpts=PTS-{Number(origin + range.Start)}/TB,aresample=48000:async=1:first_pts=0,apad,"
        + $"atrim=duration={Number(range.End - range.Start)}";
}
