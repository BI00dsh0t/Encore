using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Engine.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void DebugListTakesNoArguments()
    {
        Assert.IsType<DebugListCommand>(CommandLine.Parse(["debug-list"]).Command);
        Assert.NotNull(CommandLine.Parse(["debug-list", "--window", "x"]).Error);
    }

    [Fact]
    public void ServeTakesOnlyATestInstance()
    {
        Assert.Null(Assert.IsType<ServeCommand>(CommandLine.Parse(["serve"]).Command).Instance);
        Assert.Equal("ReplayBuffer-Test-ab1-2", Assert.IsType<ServeCommand>(CommandLine.Parse(["serve", "--instance", "ReplayBuffer-Test-ab1-2"]).Command).Instance);
        Assert.Equal(new ServeCommand(null, DebugCapture: true), CommandLine.Parse(["serve", "--debug-capture"]).Command);
        Assert.Equal(new ServeCommand("ReplayBuffer-Test-gd31", DebugCapture: true), CommandLine.Parse(["serve", "--debug-capture", "--instance", "ReplayBuffer-Test-gd31"]).Command);
        Assert.False(Assert.IsType<ServeCommand>(CommandLine.Parse(["serve"]).Command).DebugCapture);
    }

    [Theory]
    [InlineData("serve", "--instance", "ReplayBuffer")]
    [InlineData("serve", "--instance", "ReplayBuffer-Dev")]
    [InlineData("serve", "--instance", "ReplayBuffer-Test-")]
    [InlineData("serve", "--instance", "ReplayBuffer-Test-a.b")]
    [InlineData("serve", "--instance", "ReplayBuffer-Test-..\\x")]
    [InlineData("serve", "--window", "x")]
    [InlineData("serve", "--instance")]
    [InlineData("serve", "--debug-capture", "--debug-capture")]
    [InlineData("serve", "--instance", "ReplayBuffer-Test-a", "--instance", "ReplayBuffer-Test-b")]
    public void ServeRefusesAnythingElse(params string[] args)
    {
        Assert.NotNull(CommandLine.Parse(args).Error);
    }

    [Fact]
    public void DebugCaptureTakesTheTrackLayoutFlagAndATestInstance()
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(
            ["debug-capture", "--window", "League", "--track-layout", "--instance", "ReplayBuffer-Test-2-1", "--seconds", "3"]).Command);

        Assert.True(command.TrackLayout);
        Assert.Equal("ReplayBuffer-Test-2-1", command.Instance);
        Assert.Equal(3, command.Seconds);

        var plain = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(["debug-capture", "--window", "League"]).Command);
        Assert.False(plain.TrackLayout);
        Assert.Null(plain.Instance);
    }

    [Theory]
    [InlineData("--track-layout", "--output-device", "Speakers")]
    [InlineData("--input-device", "Mic", "--track-layout")]
    [InlineData("--instance", "ReplayBuffer")]
    [InlineData("--instance", "Foo")]
    [InlineData("--instance", "ReplayBuffer-Test-")]
    [InlineData("--instance", "ReplayBuffer-Test-..\\x")]
    [InlineData("--instance")]
    public void DebugCaptureRefusesMixedDeviceFlagsAndNonTestInstances(params string[] extra)
    {
        Assert.NotNull(CommandLine.Parse(["debug-capture", "--window", "League", .. extra]).Error);
    }

    [Fact]
    public void DebugCaptureDefaultsToThirtySecondsAndDefaultDevices()
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(["debug-capture", "--window", "League"]).Command);

        Assert.Equal("League", command.Window);
        Assert.Null(command.Hwnd);
        Assert.Equal(30, command.Seconds);
        Assert.Null(command.Output);
        Assert.Null(command.OutputDevice);
        Assert.Null(command.InputDevice);
        Assert.Null(command.Stats);
        Assert.Null(command.GpuPriority);
        Assert.Equal(CaptureStages.Full, command.Stages);
    }

    [Fact]
    public void DebugCaptureReadsEveryOption()
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(
        [
            "debug-capture", "--hwnd", "0x1A2B", "--seconds", "5", "--output", @"C:\x\a.mp4",
            "--output-device", "Game", "--input-device", "BEACN Mic",
        ]).Command);

        Assert.Equal((nint)0x1A2B, command.Hwnd);
        Assert.Null(command.Window);
        Assert.Equal(5, command.Seconds);
        Assert.Equal(@"C:\x\a.mp4", command.Output);
        Assert.Equal("Game", command.OutputDevice);
        Assert.Equal("BEACN Mic", command.InputDevice);
    }

    [Theory]
    [InlineData("-7", -7)]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("+3", 3)]
    public void StatsAndGpuPriorityAreRead(string priority, int expected)
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(
            ["debug-capture", "--window", "Cyberpunk", "--stats", @"C:\x\run.json", "--gpu-priority", priority]).Command);

        Assert.Equal(@"C:\x\run.json", command.Stats);
        Assert.Equal(expected, command.GpuPriority);
    }

    [Theory]
    [InlineData("full", CaptureStages.Full)]
    [InlineData("copy", CaptureStages.Copy)]
    [InlineData("wgc", CaptureStages.Wgc)]
    public void StagesAreRead(string value, CaptureStages expected)
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(["debug-capture", "--window", "a", "--stages", value]).Command);

        Assert.Equal(expected, command.Stages);
    }

    [Fact]
    public void TheEncodeExperimentOptionsDefaultToTheTracer()
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(["debug-capture", "--window", "a"]).Command);

        Assert.Equal("p5", command.NvencPreset);
        Assert.Null(command.NvencTune);
        Assert.Equal(ColorPath.X1, command.Color);
        Assert.False(command.EncodeThread);
    }

    [Fact]
    public void TheEncodeExperimentOptionsAreRead()
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(
        [
            "debug-capture", "--encode-thread", "--window", "a", "--nvenc-preset", "p1", "--nvenc-tune", "ull", "--color", "x2",
        ]).Command);

        Assert.Equal("p1", command.NvencPreset);
        Assert.Equal("ull", command.NvencTune);
        Assert.Equal(ColorPath.X2, command.Color);
        Assert.True(command.EncodeThread);
    }

    [Fact]
    public void TheVideoOptionsDefaultToTheTracerAtTheWindowsSize()
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(["debug-capture", "--window", "a"]).Command);

        Assert.Equal(VideoCodec.H264, command.Codec);
        Assert.Null(command.Size);
        Assert.Equal(60, command.Fps);
        Assert.Equal(20_000, command.BitrateKbps);
        Assert.Null(command.Encoder);
    }

    [Theory]
    [InlineData("h264", VideoCodec.H264)]
    [InlineData("hevc", VideoCodec.Hevc)]
    [InlineData("av1", VideoCodec.Av1)]
    public void TheVideoOptionsAreRead(string codec, VideoCodec expected)
    {
        var command = Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(
        [
            "debug-capture", "--window", "a", "--codec", codec, "--size", "3440x1440", "--fps", "120", "--bitrate", "60000",
            "--encoder", "libx264",
        ]).Command);

        Assert.Equal(expected, command.Codec);
        Assert.Equal(new DebugCaptureSize(3440, 1440), command.Size);
        Assert.Equal(120, command.Fps);
        Assert.Equal(60_000, command.BitrateKbps);
        Assert.Equal("libx264", command.Encoder);
    }

    [Theory]
    [InlineData("2x2")]
    [InlineData("8192x8192")]
    [InlineData("1280x720")]
    public void AnyEvenSizeUpToTheMaximumIsAccepted(string size)
    {
        Assert.IsType<DebugCaptureCommand>(CommandLine.Parse(["debug-capture", "--window", "a", "--size", size]).Command);
    }

    [Theory]
    [InlineData]
    [InlineData("debug-capture", "--window", "a", "--nvenc-preset", "p8")]
    [InlineData("debug-capture", "--window", "a", "--nvenc-preset", "slow")]
    [InlineData("debug-capture", "--window", "a", "--nvenc-tune", "lossless")]
    [InlineData("debug-capture", "--window", "a", "--color", "x3")]
    [InlineData("debug-capture", "--window", "a", "--color")]
    [InlineData("debug-capture", "--window", "a", "--stages", "encode")]
    [InlineData("debug-capture", "--window", "a", "--gpu-priority", "8")]
    [InlineData("debug-capture", "--window", "a", "--gpu-priority", "-8")]
    [InlineData("debug-capture", "--window", "a", "--gpu-priority", "high")]
    [InlineData("debug-capture", "--window", "a", "--gpu-priority", "1.5")]
    [InlineData("debug-capture", "--window", "a", "--gpu-priority")]
    [InlineData("debug-capture", "--window", "a", "--stats", "")]
    [InlineData("debug-capture", "--window", "a", "--stats")]
    [InlineData("capture")]
    [InlineData("debug-capture")]
    [InlineData("debug-capture", "--window", "a", "--hwnd", "0x10")]
    [InlineData("debug-capture", "--window", "")]
    [InlineData("debug-capture", "--window")]
    [InlineData("debug-capture", "--window", "a", "--seconds", "0")]
    [InlineData("debug-capture", "--window", "a", "--seconds", "-3")]
    [InlineData("debug-capture", "--window", "a", "--seconds", "ten")]
    [InlineData("debug-capture", "--hwnd", "0")]
    [InlineData("debug-capture", "--hwnd", "0xZZ")]
    [InlineData("debug-capture", "--window", "a", "--fps", "144")]
    [InlineData("debug-capture", "--window", "a", "--fps", "0")]
    [InlineData("debug-capture", "--window", "a", "--fps", "sixty")]
    [InlineData("debug-capture", "--window", "a", "--fps")]
    [InlineData("debug-capture", "--window", "a", "--codec", "vp9")]
    [InlineData("debug-capture", "--window", "a", "--codec", "H264")]
    [InlineData("debug-capture", "--window", "a", "--size", "1920")]
    [InlineData("debug-capture", "--window", "a", "--size", "1921x1080")]
    [InlineData("debug-capture", "--window", "a", "--size", "1920x1081")]
    [InlineData("debug-capture", "--window", "a", "--size", "0x0")]
    [InlineData("debug-capture", "--window", "a", "--size", "8194x1080")]
    [InlineData("debug-capture", "--window", "a", "--size", "1920x1080x2")]
    [InlineData("debug-capture", "--window", "a", "--size", "-1920x1080")]
    [InlineData("debug-capture", "--window", "a", "--bitrate", "0")]
    [InlineData("debug-capture", "--window", "a", "--bitrate", "20M")]
    [InlineData("debug-capture", "--window", "a", "--encoder", "")]
    [InlineData("debug-capture", "--window", "a", "--encoder")]
    [InlineData("debug-capture", "--window", "a", "--rate-control", "vbr")]
    public void MalformedCommandLinesAreRefused(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.Null(result.Command);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Theory]
    [InlineData("0x1a2b", 0x1A2B)]
    [InlineData("0X00010C7E", 0x10C7E)]
    [InlineData("68734", 68734)]
    public void HandlesParseAsHexOrDecimal(string text, long expected)
    {
        Assert.True(CommandLine.TryParseHandle(text, out var handle));
        Assert.Equal((nint)expected, handle);
    }
}

public sealed class OutputNamingTests
{
    [Theory]
    [InlineData("League of Legends", "League of Legends")]
    [InlineData("Discord | #general: chat", "Discord _ #general_ chat")]
    [InlineData("a/b\\c*d?e\"f<g>h", "a_b_c_d_e_f_g_h")]
    [InlineData("  trailing dots... ", "trailing dots")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("???", "capture")]
    [InlineData("", "capture")]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    public void TitlesAreMadeSafeAsFileNames(string title, string expected)
    {
        Assert.Equal(expected, OutputNaming.Sanitize(title));
    }

    [Fact]
    public void LongTitlesAreCut()
    {
        Assert.Equal(100, OutputNaming.Sanitize(new string('x', 300)).Length);
    }

    [Fact]
    public void ThePathIsTitleAndLocalStartTime()
    {
        var path = OutputNaming.Allocate(@"C:\out", "Game: One", new DateTime(2026, 10, 25, 9, 5, 7), _ => false);

        Assert.Equal(@"C:\out\Game_ One_2026-10-25_09-05-07.mp4", path);
    }

    [Fact]
    public void CollisionsGetNumberSuffixes()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\out\Game_2026-10-25_09-05-07.mp4",
            @"C:\out\Game_2026-10-25_09-05-07_2.mp4",
        };

        var path = OutputNaming.Allocate(@"C:\out", "Game", new DateTime(2026, 10, 25, 9, 5, 7), taken.Contains);

        Assert.Equal(@"C:\out\Game_2026-10-25_09-05-07_3.mp4", path);
    }
}
