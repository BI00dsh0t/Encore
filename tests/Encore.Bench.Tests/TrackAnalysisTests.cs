namespace Encore.Bench.Tests;

/// <summary><c>bench tracks</c>' checks, from synthetic probe data, onsets, levels and peaks.</summary>
public sealed class TrackAnalysisTests
{
    private const double Video = 300;
    private const double Tone = 0;

    [Fact]
    public void AGoodFilePasses()
    {
        var result = Evaluate(Probes(), Signals(), Vlc(TrackAnalysis.ExpectedTitles));

        Assert.Equal(TrackAnalysis.PassVerdict, result.Verdict);
        Assert.All(result.Checks, c => Assert.Equal(DriftMath.Pass, c.Mark));
        Assert.Equal(["titles", "default", "durations", "sync", "level", "fullScale", "vlc"], result.Checks.Select(c => c.Check));
        var sync = result.Sync!;
        Assert.Equal(1.0, sync.MatchedFraction);
        Assert.InRange(sync.MaxMs!.Value, 0, 0.1);
        Assert.Equal(299, sync.GameExpected);
        Assert.Equal(298, sync.ChatExpected);
        Assert.Equal(299 + 298, result.Tracks[TrackAnalysis.Everything].ExpectedOnsets);
        Assert.Equal(299, result.Tracks[TrackAnalysis.Game].ExpectedOnsets);
        Assert.Null(result.Tracks[3].ExpectedOnsets);
        Assert.Equal(-0.1, result.Tracks[TrackAnalysis.Game].BurstLevelDbfs);
    }

    [Theory]
    [InlineData("Everything", "Chat", "Game", "Mic")]
    [InlineData("Everything", "Game", "Chat", "Microphone")]
    public void WrongNamesOrOrderFailTitles(string a, string b, string c, string? d)
    {
        var probes = Probes();
        string?[] titles = [a, b, c, d];
        probes = [.. probes.Select((p, i) => p with { Title = titles[i] })];

        var result = Evaluate(probes, Signals(), null);

        Assert.Equal(DriftMath.Miss, Check(result, "titles").Mark);
        Assert.Equal(TrackAnalysis.FailVerdict, result.Verdict);
    }

    [Fact]
    public void TooFewTracksFailTitles()
    {
        var result = Evaluate(Probes().Take(3).ToList(), Signals(), null);

        Assert.Equal(DriftMath.Miss, Check(result, "titles").Mark);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    public void AWrongDefaultFails(bool d0, bool d1, bool d2, bool d3)
    {
        bool[] defaults = [d0, d1, d2, d3];
        var probes = Probes().Select((p, i) => p with { Default = defaults[i] }).ToList();

        var result = Evaluate(probes, Signals(), null);

        Assert.Equal(DriftMath.Miss, Check(result, "default").Mark);
        Assert.Equal(TrackAnalysis.FailVerdict, result.Verdict);
    }

    [Fact]
    public void AFiveMillisecondShiftFailsSync()
    {
        var signals = Signals();
        signals[TrackAnalysis.Game] = signals[TrackAnalysis.Game]! with { OnsetSeconds = [.. signals[TrackAnalysis.Game]!.OnsetSeconds.Select(t => t + 0.005)] };

        var result = Evaluate(Probes(), signals, null);

        Assert.Equal(DriftMath.Miss, Check(result, "sync").Mark);
        Assert.Equal(TrackAnalysis.FailVerdict, result.Verdict);
    }

    [Fact]
    public void ATwoMillisecondShiftIsMatchedButOverTheBound()
    {
        var signals = Signals();
        signals[TrackAnalysis.Chat] = signals[TrackAnalysis.Chat]! with { OnsetSeconds = [.. signals[TrackAnalysis.Chat]!.OnsetSeconds.Select(t => t - 0.002)] };

        var result = Evaluate(Probes(), signals, null);

        Assert.Equal(1.0, result.Sync!.MatchedFraction);
        Assert.InRange(result.Sync.MaxMs!.Value, 1.9, 2.1);
        Assert.Equal(DriftMath.Miss, Check(result, "sync").Mark);
    }

    [Fact]
    public void ALevelThreeDecibelsLowFails()
    {
        var signals = Signals();
        signals[TrackAnalysis.Game] = signals[TrackAnalysis.Game]! with { BurstLevelsDbfs = [.. signals[TrackAnalysis.Game]!.BurstLevelsDbfs.Select(l => l - 3)] };

        var result = Evaluate(Probes(), signals, null);

        Assert.Equal(DriftMath.Miss, Check(result, "level").Mark);
        Assert.Contains("Game -3.1 dBFS", Check(result, "level").Figure, StringComparison.Ordinal);
    }

    [Fact]
    public void ARareMaxOverFullScaleIsInformationOnly()
    {
        // As the real capture: AAC rings to 2.02 at the bursts' edges while 99.9 % of samples stay under full scale.
        var signals = Signals();
        signals[TrackAnalysis.Everything] = signals[TrackAnalysis.Everything]! with { Peak = 2.02, P999 = 0.9969 };

        var result = Evaluate(Probes(), signals, null);

        Assert.Equal(DriftMath.Pass, Check(result, "fullScale").Mark);
        Assert.Contains("max 2.02", Check(result, "fullScale").Figure, StringComparison.Ordinal);
        Assert.Equal(2.02, result.Tracks[TrackAnalysis.Everything].Peak);
    }

    [Fact]
    public void ANinetyNinePointNinthPercentileOverFullScaleFails()
    {
        var signals = Signals();
        signals[TrackAnalysis.Everything] = signals[TrackAnalysis.Everything]! with { Peak = 1.3, P999 = 1.2 };

        var result = Evaluate(Probes(), signals, null);

        Assert.Equal(DriftMath.Miss, Check(result, "fullScale").Mark);
        Assert.Equal(TrackAnalysis.FailVerdict, result.Verdict);
    }

    [Fact]
    public void AShortTrackFailsDurations()
    {
        var probes = Probes();
        probes[3] = probes[3] with { DurationSeconds = Video - 0.2 };

        var result = Evaluate(probes, Signals(), null);

        Assert.Equal(DriftMath.Miss, Check(result, "durations").Mark);
    }

    [Fact]
    public void TooFewOnsetsInASingleDeviceTrackFailSync()
    {
        var signals = Signals();

        // Every onset still matched, but Chat has only 80 % of its bursts.
        var chat = signals[TrackAnalysis.Chat]!.OnsetSeconds.Take(240).ToList();
        signals[TrackAnalysis.Chat] = signals[TrackAnalysis.Chat]! with { OnsetSeconds = chat };

        var result = Evaluate(Probes(), signals, null);

        Assert.Equal(1.0, result.Sync!.MatchedFraction);
        Assert.Equal(DriftMath.Miss, Check(result, "sync").Mark);
        Assert.Contains("Chat 240/298", Check(result, "sync").Figure, StringComparison.Ordinal);
    }

    [Fact]
    public void TooFewMatchedInEverythingFailSync()
    {
        var signals = Signals();
        var everything = signals[TrackAnalysis.Everything]!.OnsetSeconds.Where((_, i) => i % 10 != 0).ToList();
        signals[TrackAnalysis.Everything] = signals[TrackAnalysis.Everything]! with { OnsetSeconds = everything };

        var result = Evaluate(Probes(), signals, null);

        Assert.InRange(result.Sync!.MatchedFraction, 0.85, 0.95);
        Assert.Equal(DriftMath.Miss, Check(result, "sync").Mark);
    }

    [Fact]
    public void AbsentVlcIsNotCheckedAndIgnored()
    {
        var result = Evaluate(Probes(), Signals(), new VlcListing(false, null, "VLC not found"));

        Assert.Equal(TrackAnalysis.NotChecked, Check(result, "vlc").Mark);
        Assert.Equal(TrackAnalysis.PassVerdict, result.Verdict);
        Assert.Equal(TrackAnalysis.NotChecked, Check(Evaluate(Probes(), Signals(), null), "vlc").Mark);
    }

    [Fact]
    public void VlcListingOtherNamesFails()
    {
        var result = Evaluate(Probes(), Signals(), Vlc(["Track 1", "Track 2", "Track 3", "Track 4"]));

        Assert.Equal(DriftMath.Miss, Check(result, "vlc").Mark);
        Assert.Equal(TrackAnalysis.FailVerdict, result.Verdict);
    }

    [Fact]
    public void VlcAudioLabelsAreTheAudioStreamsDescriptionsInOrder()
    {
        const string Status = """
            { "information": { "category": {
                "meta": { "filename": "tracks.mp4" },
                "Stream 10": { "Type": "Audio", "Description": "Mic" },
                "Stream 0": { "Type": "Video", "Codec": "H264" },
                "Stream 2": { "Type": "Audio", "Description": "Game" },
                "Stream 1": { "Type": "Audio", "Description": "Everything" },
                "Stream 3": { "Type": "Audio", "Description": "Chat" } } } }
            """;

        Assert.Equal(["Everything", "Game", "Chat", "Mic"], PlayerScripts.AudioLabels(Status));
        Assert.Empty(PlayerScripts.AudioLabels("""{ "state": "stopped" }"""));
    }

    [Fact]
    public void TheScannerFindsBurstsAndTheirLevel()
    {
        // Two seconds of 440 Hz at -10 dB under a 0.5-amplitude 3 kHz burst at 0.25 s and 1.25 s, fed in odd chunks.
        const int Rate = OnsetDetector.SampleRate;
        var samples = new float[2 * 2 * Rate];
        for (var n = 0; n < 2 * Rate; n++)
        {
            var t = (double)n / Rate;
            var inBurst = (t % 1) is >= 0.25 and < 0.30;
            var x = inBurst ? 0.5 * Math.Sin(2 * Math.PI * 3000 * t) : 0.158 * Math.Sin(2 * Math.PI * 440 * t);
            samples[2 * n] = samples[(2 * n) + 1] = (float)x;
        }

        var scanner = new TrackSignalScanner(-6);
        for (var at = 0; at < samples.Length; at += 2 * 777)
        {
            scanner.FeedStereo(samples.AsSpan(at, Math.Min(2 * 777, samples.Length - at)));
        }

        var signal = scanner.Result(0);

        Assert.Equal(2, signal.OnsetSeconds.Count);
        Assert.InRange(signal.OnsetSeconds[0], 0.25, 0.252);
        Assert.InRange(signal.OnsetSeconds[1], 1.25, 1.252);
        Assert.All(signal.BurstLevelsDbfs, l => Assert.InRange(l, -6.1, -5.9));
        Assert.InRange(signal.Peak, 0.49, 0.51);
        Assert.InRange(signal.P999, 0.49, 0.51);
    }

    [Fact]
    public void RareIsolatedSpikesPassFullScale()
    {
        // 0.9-amplitude bursts each second, with one 2.0 spike at each burst's edge.
        var signal = Scan(burstAmplitude: 0.9, spike: 2.0);

        Assert.Equal(2.0, signal.Peak, 3);
        Assert.InRange(signal.P999, 0.85, 0.95);
        Assert.Equal(DriftMath.Pass, Check(Evaluate(Probes(), WithEverything(signal), null), "fullScale").Mark);
    }

    [Fact]
    public void SustainedBurstsOverFullScaleFail()
    {
        var signal = Scan(burstAmplitude: 1.25, spike: null);

        Assert.InRange(signal.P999, 1.2, 1.3);
        Assert.Equal(DriftMath.Miss, Check(Evaluate(Probes(), WithEverything(signal), null), "fullScale").Mark);
    }

    /// <summary>Ten seconds of a 3 kHz burst for 50 ms each second over a 440 Hz tone at 0.3.</summary>
    private static TrackSignal Scan(double burstAmplitude, double? spike)
    {
        const int Rate = OnsetDetector.SampleRate;
        var samples = new float[2 * 10 * Rate];
        for (var n = 0; n < 10 * Rate; n++)
        {
            var t = (double)n / Rate;
            var inBurst = (t % 1) is >= 0.25 and < 0.30;
            var x = inBurst ? burstAmplitude * Math.Sin(2 * Math.PI * 3000 * t) : 0.3 * Math.Sin(2 * Math.PI * 440 * t);
            if (spike is { } s && n % Rate == Rate / 4)
            {
                x = s;
            }

            samples[2 * n] = samples[(2 * n) + 1] = (float)x;
        }

        var scanner = new TrackSignalScanner(0);
        scanner.FeedStereo(samples);
        return scanner.Result(0);
    }

    private static List<TrackSignal?> WithEverything(TrackSignal everything)
    {
        var signals = Signals();
        signals[TrackAnalysis.Everything] = signals[TrackAnalysis.Everything]! with { Peak = everything.Peak, P999 = everything.P999 };
        return signals;
    }

    [Fact]
    public void TheStoredLayoutIsTheFourTracksOverTheThreeDevices()
    {
        var a = new Platform.Audio.AudioEndpoint("{a}", "Speakers A", Platform.Audio.AudioFlow.Output);
        var b = new Platform.Audio.AudioEndpoint("{b}", "Speakers B", Platform.Audio.AudioFlow.Output);
        var mic = new Platform.Audio.AudioEndpoint("{m}", "Mic", Platform.Audio.AudioFlow.Input);

        var json = TracksCheck.SettingsJson(TracksCheck.Layout(a, b, mic));

        var document = System.Text.Json.JsonSerializer.Deserialize(json, Contracts.Settings.SettingsJsonContext.Default.SettingsDocument)!;
        var raw = document.Global[Contracts.Settings.Keys.TrackLayout.Name];
        Assert.True(Contracts.Settings.Keys.TrackLayout.IsValid(raw));
        var layout = System.Text.Json.JsonSerializer.Deserialize(raw, Contracts.Settings.SettingsJsonContext.Default.TrackLayout)!;
        Assert.Equal(TrackAnalysis.ExpectedTitles, layout.Tracks.Select(t => t.Name));
        Assert.Equal(Contracts.Settings.Keys.EverythingTrackId, layout.Tracks[0].Id);
        string Endpoints(int track) => string.Join(",", layout.Tracks[track].DeviceKeys.Select(k => layout.Devices.Single(d => d.Key == k).EndpointId));
        Assert.Equal("{a},{b},{m}", Endpoints(0));
        Assert.Equal("{a}", Endpoints(1));
        Assert.Equal("{b}", Endpoints(2));
        Assert.Equal("{m}", Endpoints(3));
        Assert.True(layout.Devices.Single(d => d.EndpointId == "{m}").IsMic);
    }

    [Theory]
    [InlineData(0, 5_000_000, 0.5)]
    [InlineData(0, 25_000_000, 0.5)]
    public void PhaseIsWhereBFallsInAsSecondGrid(long a, long b, double phase) =>
        Assert.Equal(phase, TracksCheck.Phase(a, b, 10_000_000), 6);

    private static TracksAnalysis Evaluate(List<TrackProbe> probes, List<TrackSignal?> signals, VlcListing? vlc) =>
        TrackAnalysis.Evaluate(Video, probes, signals, Tone, vlc);

    private static NamedTrackCheck Check(TracksAnalysis result, string name) => result.Checks.Single(c => c.Check == name);

    private static VlcListing Vlc(IReadOnlyList<string> labels) => new(true, labels, null);

    private static List<TrackProbe> Probes() =>
    [
        new("Everything", true, 0, Video + 0.02),
        new("Game", false, 0, Video + 0.02),
        new("Chat", false, 0, Video - 0.01),
        new("Mic", false, 0, Video),
    ];

    /// <summary>Game's bursts at 0.3 + k s, Chat's at 0.8 + k s, both in Everything 0.05 ms later; 300 of each.</summary>
    private static List<TrackSignal?> Signals()
    {
        var game = Enumerable.Range(0, 300).Select(k => 0.3 + k).ToList();
        var chat = Enumerable.Range(0, 300).Select(k => 0.8 + k).ToList();
        var everything = game.Concat(chat).Select(t => t + 0.00005).Order().ToList();
        var levels = Enumerable.Range(0, 300).Select(k => k % 3 == 0 ? -0.12 : -0.1).ToList();
        return
        [
            new TrackSignal(everything, [.. Enumerable.Repeat(-2.5, 600)], 0.999, 0.99),
            new TrackSignal(game, levels, 0.99, 0.98),
            new TrackSignal(chat, levels, 0.99, 0.98),
            null,
        ];
    }
}
