using System.Text.Json;

namespace Encore.Bench;

/// <summary>One player's answers; Status is <c>checked</c> or <c>not-checked</c>, and Method says who checked it.</summary>
internal sealed record PlayerCheck(string Player, string Status, bool? Plays, bool? AvInSync, string? AudioTracks, string? Notes, string? Method = null);

/// <summary>The <c>playback.json</c> report; CaptureBorder is yes, no or not-checked.</summary>
internal sealed record PlaybackReport(
    DateTimeOffset Created,
    string Clip,
    int Seconds,
    CaptureSettings Capture,
    int AudioTracks,
    string? CaptureBorder,
    IReadOnlyList<PlayerCheck> Players,
    IReadOnlyList<string> Warnings,
    bool? Auto = null,
    BorderProbeResult? BorderProbe = null,
    IReadOnlyList<CodecPlayback>? Extras = null);

/// <summary>One more file the players were given; Encoded is false, with an error, when the GPU couldn't encode the codec.</summary>
internal sealed record CodecPlayback(string Name, string Codec, string? File, bool Encoded, string? Error, int AudioTracks, IReadOnlyList<string> TrackTitles, IReadOnlyList<PlayerCheck> Players);

/// <summary><c>bench playback</c>: a short marked capture, then a checklist per player; <c>--auto</c> scripts what it can.</summary>
internal static class PlaybackCheck
{
    public const string FileName = "playback.json";

    public static readonly IReadOnlyList<string> DefaultPlayers = ["Discord", "Chrome", "VLC", "Windows Media Player", "DaVinci Resolve"];

    /// <summary>The codecs <c>--codecs</c> makes clips of by default: H.264, then HEVC and AV1 where the GPU encodes them.</summary>
    public static readonly IReadOnlyList<string> DefaultCodecs = ["h264", "hevc", "av1"];

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        var seconds = options.Int("seconds", 30, 5, 600);
        var players = options.List("players", DefaultPlayers);
        var settings = options.CaptureSettings();
        var toneDbfs = options.Double("tone-dbfs", OnsetDetector.DefaultBurstDbfs, -60, -3);
        var auto = options.Flag("auto");
        if (!auto && new[] { "chrome", "play-seconds", "player-timeout" }.FirstOrDefault(options.Has) is { } autoOption)
        {
            throw new BenchUsageException($"--{autoOption} applies to --auto only.");
        }

        var playSeconds = options.Int("play-seconds", 5, 1, 120);
        var playerTimeout = TimeSpan.FromSeconds(options.Int("player-timeout", 60, 10, 600));
        var chromeOption = options.Text("chrome");
        var codecs = options.List("codecs", DefaultCodecs).Select(c => c.ToLowerInvariant()).Distinct().ToList();
        if (codecs.FirstOrDefault(c => !DefaultCodecs.Contains(c)) is { } badCodec)
        {
            throw new BenchUsageException($"--codecs '{badCodec}' is not one of {string.Join(", ", DefaultCodecs)}.");
        }

        if (!codecs.Contains("h264"))
        {
            throw new BenchUsageException("--codecs must include h264: its clip is the one every player is checked with.");
        }

        string? recordingFile = null;
        if (options.Text("recording-file") is { } given)
        {
            if (!File.Exists(given))
            {
                throw new BenchFailure($"--recording-file '{given}' does not exist.");
            }

            recordingFile = Path.GetFullPath(given);
        }

        var engine = context.ResolveEngine();
        var tools = context.ResolveFfmpeg();
        var clip = Path.Combine(context.RunFolder, "playback.mp4");
        var warnings = new List<string>();

        Console.WriteLine($"Playback: a {seconds} s clip of the marker window with both tracks, into {clip}");
        var probe = auto ? new BorderProbe(context.RunFolder, TimeSpan.FromSeconds(Math.Min(8, seconds / 2.0))) : null;
        if (!auto)
        {
            Console.WriteLine("Watch the marker window while it records: does a yellow capture border show around it?");
        }

        var (exit, stderr, log) = MarkedCapture.Run(engine, settings, seconds, clip, null, Path.Combine(context.RunFolder, "playback"), toneDbfs, warnings, probe);
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
        PlaybackReport report;
        if (auto)
        {
            var borderProbe = probe!.Result();
            Console.WriteLine($"Capture border: {borderProbe.Verdict} ({borderProbe.YellowBefore} yellow pixels before the capture, {borderProbe.YellowDuring} during, of {borderProbe.RingPixels} round the edge{(borderProbe.Error is null ? "" : "; " + borderProbe.Error)}).");
            var checks = Scripted(players, clip, context.RunFolder, chromeOption, playSeconds, playerTimeout);
            report = new PlaybackReport(DateTimeOffset.Now, clip, seconds, settings, audioTracks, borderProbe.Verdict, checks, warnings, true, borderProbe);
        }
        else
        {
            Console.WriteLine("The marker flashes white on each burst. Answer y or n; an empty answer or 'skip' leaves it not checked.");
            Console.WriteLine();
            var (border, checks) = Questionnaire(players, Console.In, Console.Out);
            report = new PlaybackReport(DateTimeOffset.Now, clip, seconds, settings, audioTracks, border, checks, warnings, false);
        }

        var extras = new List<CodecPlayback>();
        foreach (var codec in codecs.Where(c => c != "h264"))
        {
            var file = Path.Combine(context.RunFolder, $"playback-{codec}.mp4");
            Console.WriteLine();
            Console.WriteLine($"Playback: a {seconds} s {codec.ToUpperInvariant()} clip, into {file}");
            var (codecExit, codecError, _) = MarkedCapture.Run(engine, settings, seconds, file, null, Path.Combine(context.RunFolder, "playback-" + codec), toneDbfs, warnings, codec: codec);
            if (codecExit != 0 || !File.Exists(file))
            {
                var why = codecError.Length > 0 ? codecError : $"exit {codecExit}";
                Console.WriteLine($"  {codec}: not encoded on this GPU ({why}).");
                extras.Add(new CodecPlayback(codec, codec, null, false, why, 0, [], []));
                continue;
            }

            extras.Add(Extra(codec, codec, file, tools, toneDbfs, auto, players, context.RunFolder, chromeOption, playSeconds, playerTimeout, warnings));
        }

        if (recordingFile is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Playback: the Recording {recordingFile}");
            extras.Add(Extra("recording", "h264", recordingFile, tools, toneDbfs, auto, players, context.RunFolder, chromeOption, playSeconds, playerTimeout, warnings));
        }

        BenchJson.Write(Path.Combine(context.RunFolder, FileName), report with { Extras = extras });
        return 0;
    }

    /// <summary>One extra file: its Tracks from ffprobe, then the scripted players, or every player needing a person.</summary>
    private static CodecPlayback Extra(
        string name, string codec, string file, FfmpegTools tools, double toneDbfs, bool auto, IReadOnlyList<string> players, string work, string? chrome, int playSeconds, TimeSpan timeout,
        List<string> warnings)
    {
        var tracks = 0;
        IReadOnlyList<string> titles = [];
        try
        {
            var scan = MediaAnalysis.Scan(tools, file, 0, toneDbfs, CancellationToken.None).AudioStreams.ToList();
            tracks = scan.Count;
            titles = [.. scan.Select(s => s.Title ?? "")];
        }
        catch (BenchFailure e)
        {
            warnings.Add($"Could not probe {file}: {e.Message}");
        }

        Console.WriteLine($"  {tracks} audio track(s): {string.Join(", ", titles)}");
        var checks = auto ? Scripted(players, file, Directory.CreateDirectory(Path.Combine(work, "playback-" + name)).FullName, chrome, playSeconds, timeout) : [.. players.Select(PlayerScripts.NeedsPerson)];
        return new CodecPlayback(name, codec, file, true, null, tracks, titles, checks);
    }

    /// <summary>The <c>--auto</c> checklist: Chrome and Windows Media Player scripted, every other player needing a person.</summary>
    internal static List<PlayerCheck> Scripted(IReadOnlyList<string> players, string clip, string work, string? chromeOption, int playSeconds, TimeSpan timeout)
    {
        var checks = new List<PlayerCheck>();
        foreach (var player in players)
        {
            ScriptedPlayback? result = null;
            if (player.Equals(PlayerScripts.Chrome, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{player}: playing the clip headless for {playSeconds} s ...");
                if (PlayerScripts.FindChrome(chromeOption) is { } chrome)
                {
                    result = PlayerScripts.RunChrome(chrome, clip, work, playSeconds, timeout);
                }
                else
                {
                    var why = chromeOption is null ? "Chrome is not installed (pass --chrome <exe>)" : $"Chrome is not at '{chromeOption}'";
                    result = new ScriptedPlayback(PlayerScripts.Chrome, null, null, null, null, null, null, null, null, null, null, why);
                }
            }
            else if (player.Equals(PlayerScripts.WindowsMediaPlayer, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{player}: playing the clip through WMPlayer.OCX for {playSeconds} s ...");
                result = PlayerScripts.RunWindowsMediaPlayer(clip, work, playSeconds, timeout);
            }

            var check = result is null ? PlayerScripts.NeedsPerson(player) : PlayerScripts.ToCheck(result with { Player = player });
            var playsText = "";
            if (check.Plays is { } plays)
            {
                playsText = plays ? ", plays" : ", does not play";
            }

            Console.WriteLine($"  {player}: {check.Status}{playsText}; {check.Notes}");
            checks.Add(check);
        }

        return checks;
    }

    /// <summary>The checklist: the capture border, then each player; a skipped answer records <c>not-checked</c>.</summary>
    internal static (string Border, List<PlayerCheck> Players) Questionnaire(IReadOnlyList<string> players, TextReader input, TextWriter output)
    {
        var border = Ask("Did a capture border show around the marker window while it recorded? (y/n/skip)", input, output) switch
        {
            true => "yes",
            false => "no",
            null => "not-checked",
        };

        var checks = new List<PlayerCheck>();
        foreach (var player in players)
        {
            output.WriteLine();
            output.WriteLine($"--- {player}: open the clip in {player}.");
            var plays = Ask($"{player}: does it play? (y/n/skip)", input, output);
            if (plays is null)
            {
                checks.Add(new PlayerCheck(player, "not-checked", null, null, null, null, PlayerScripts.Person));
                continue;
            }

            var sync = plays.Value ? Ask($"{player}: are the flashes and bursts in sync? (y/n/skip)", input, output) : null;
            var tracks = plays.Value ? Line($"{player}: which audio tracks can it play or list? (e.g. 'both', 'first only')", input, output) : null;
            var notes = Line($"{player}: notes (optional)", input, output);
            checks.Add(new PlayerCheck(player, "checked", plays, sync, tracks, notes, PlayerScripts.Person));
        }

        return (border, checks);
    }

    /// <summary>y → true, n → false, empty, skip or end of input → null.</summary>
    internal static bool? ParseAnswer(string? line) => line?.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" => true,
        "n" or "no" => false,
        _ => null,
    };

    private static bool? Ask(string question, TextReader input, TextWriter output)
    {
        while (true)
        {
            output.Write(question + " ");
            var line = input.ReadLine();
            var answer = ParseAnswer(line);
            if (answer is not null || line is null || line.Trim().Length == 0 || line.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                return answer;
            }

            output.WriteLine("Answer y, n or skip.");
        }
    }

    private static string? Line(string question, TextReader input, TextWriter output)
    {
        output.Write(question + " ");
        var line = input.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }
}
