using System.Text.Json;

namespace ReplayBuffer.Bench;

/// <summary>One player's answers; <see cref="Status"/> is <c>checked</c> or <c>not-checked</c> (skipped).</summary>
internal sealed record PlayerCheck(string Player, string Status, bool? Plays, bool? AvInSync, string? AudioTracks, string? Notes);

/// <summary><c>playback.json</c>.</summary>
/// <param name="CaptureBorder">Whether WGC's capture border showed around the marker window (§7.4 <c>IsBorderRequired</c>): yes, no or not-checked.</param>
internal sealed record PlaybackReport(
    DateTimeOffset Created,
    string Clip,
    int Seconds,
    CaptureSettings Capture,
    int AudioTracks,
    string? CaptureBorder,
    IReadOnlyList<PlayerCheck> Players,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <c>bench playback</c>: a short marked capture (both tracks, the default H.264 output), then a checklist
/// the builder answers per NFR-4 player. HEVC and AV1 are not checked: they are labelled as possibly not
/// playing everywhere.
/// </summary>
internal static class PlaybackCheck
{
    public const string FileName = "playback.json";

    public static readonly IReadOnlyList<string> DefaultPlayers = ["Discord", "Chrome", "VLC", "Windows Media Player", "DaVinci Resolve"];

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        var seconds = options.Int("seconds", 30, 5, 600);
        var players = options.List("players", DefaultPlayers);
        var settings = options.CaptureSettings();
        var toneDbfs = options.Double("tone-dbfs", OnsetDetector.DefaultBurstDbfs, -60, -3);
        var engine = context.ResolveEngine();
        var tools = context.ResolveFfmpeg();
        var clip = Path.Combine(context.RunFolder, "playback.mp4");
        var warnings = new List<string>();

        Console.WriteLine($"Playback: a {seconds} s clip of the marker window with both tracks, into {clip}");
        Console.WriteLine("Watch the marker window while it records: does a yellow capture border show around it?");
        var (exit, stderr, log) = MarkedCapture.Run(engine, settings, seconds, clip, null, Path.Combine(context.RunFolder, "playback"), toneDbfs, warnings);
        File.WriteAllText(Path.Combine(context.RunFolder, "playback-markers.json"), JsonSerializer.Serialize(log, BenchJson.Options));
        if (exit != 0 || !File.Exists(clip))
        {
            Console.Error.WriteLine($"The engine failed: {(stderr.Length > 0 ? stderr : $"exit {exit}")}");
            return 1;
        }

        var audioTracks = 0;
        try
        {
            audioTracks = MediaAnalysis.Scan(tools, clip, 0, toneDbfs, CancellationToken.None).AudioStreams.Count();
        }
        catch (BenchFailure e)
        {
            warnings.Add($"Could not probe the clip: {e.Message}");
        }

        Console.WriteLine();
        Console.WriteLine($"Clip: {clip}");
        Console.WriteLine($"It has {audioTracks} audio track(s): the Output Device ({log.OutputDevice}) with a 3 kHz burst each second, and the Input Device ({log.InputDevice}).");
        Console.WriteLine("The marker flashes white on each burst. Answer y or n; an empty answer or 'skip' leaves it not checked.");
        Console.WriteLine();

        var border = Ask("Did a capture border show around the marker window while it recorded? (y/n/skip)") switch
        {
            true => "yes",
            false => "no",
            null => "not-checked",
        };

        var checks = new List<PlayerCheck>();
        foreach (var player in players)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {player}: open the clip in {player}.");
            var plays = Ask($"{player}: does it play? (y/n/skip)");
            if (plays is null)
            {
                checks.Add(new PlayerCheck(player, "not-checked", null, null, null, null));
                continue;
            }

            var sync = plays.Value ? Ask($"{player}: are the flashes and bursts in sync? (y/n/skip)") : null;
            var tracks = plays.Value ? Line($"{player}: which audio tracks can it play or list? (e.g. 'both', 'first only')") : null;
            var notes = Line($"{player}: notes (optional)");
            checks.Add(new PlayerCheck(player, "checked", plays, sync, tracks, notes));
        }

        var report = new PlaybackReport(DateTimeOffset.Now, clip, seconds, settings, audioTracks, border, checks, warnings);
        BenchJson.Write(Path.Combine(context.RunFolder, FileName), report);
        return 0;
    }

    /// <summary>y → true, n → false, empty, skip or end of input → null.</summary>
    internal static bool? ParseAnswer(string? line) => line?.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" => true,
        "n" or "no" => false,
        _ => null,
    };

    private static bool? Ask(string question)
    {
        while (true)
        {
            Console.Write(question + " ");
            var line = Console.ReadLine();
            var answer = ParseAnswer(line);
            if (answer is not null || line is null || line.Trim().Length == 0 || line.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                return answer;
            }

            Console.WriteLine("Answer y, n or skip.");
        }
    }

    private static string? Line(string question)
    {
        Console.Write(question + " ");
        var line = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }
}
