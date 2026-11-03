using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Tests;

public sealed partial class MessagesTests
{
    private static readonly Warning ActiveWarning = new("w1", ErrorCodes.SettingsUnreadable, WarningSeverity.Error, "settings", "m", DateTimeOffset.UnixEpoch, false, null, true);

    /// <summary>A snapshot every sample payload changes.</summary>
    private static readonly StateSnapshot Base = StateSnapshot.Initial("engine", 1, SettingsDocument.Empty) with { Warnings = [ActiveWarning] };

    /// <summary>One payload per registered event. A new event needs a sample here, or the coverage test fails.</summary>
    private static readonly Dictionary<string, string> Samples = new(StringComparer.Ordinal)
    {
        ["settings.changed"] = """{"revision":1,"document":{"schemaVersion":1,"revision":1,"global":{"saveCooldown":7},"profiles":{}}}""",
        ["capture.stateChanged"] = """{"replay":"buffering","recording":"running","monitorRecording":"off","micMuted":false,"cooldownUntil":null}""",
        ["clip.saved"] = """{"path":"C:\\Videos\\Game\\Game_2026-11-03_10-00-00.mp4"}""",
        ["clip.saving"] = """{"at":"2026-11-03T10:00:00+00:00"}""",
        ["clip.failed"] = """{"reason":"clip-too-short"}""",
        ["capture.videoOptions"] = """{"probed":true,"codecs":[{"codec":"h264","encoder":"h264_nvenc","mayNotPlayEverywhere":false},{"codec":"av1","encoder":"av1_nvenc","mayNotPlayEverywhere":true}],"resolutions":[{"width":1920,"height":1080}],"maxFps":120}""",
        ["warning.raised"] = """{"id":"w2","code":"settings-unreadable","severity":"warning","source":"settings","message":"m","at":"2026-10-25T12:00:00+00:00","overlay":true,"sound":null,"dismissable":true}""",
        ["warning.dismissed"] = """{"id":"w1"}""",
        ["engine.heartbeat"] = """{"at":"2026-10-25T12:00:02+00:00"}""",
        ["app.attached"] = """{"pid":7}""",
        ["hotkey.status"] = """{"bindings":[{"action":"save","binding":{"vk":135,"modifiers":7},"registered":true,"code":null},{"action":"micMute","binding":null,"registered":false,"code":"hotkey-unbound"}]}""",
        ["hotkey.pressed"] = """{"action":"monitorRecordToggle","qpc":123456789}""",
        ["devices.changed"] = """{"devices":[{"endpointId":"{0.0.0.00000000}.{a}","friendlyName":"Speakers","parentName":"Realtek","flow":"output","available":true},{"endpointId":"{0.0.1.00000000}.{b}","friendlyName":"Mic","parentName":"","flow":"input","available":false}]}""",
        ["meters.levels"] = """{"tracks":[{"trackId":"6f1c2a4e-0b7d-4e55-9a51-2f4c8d9e1a30","peak":0.5},{"trackId":"0d3e7b52-8c1a-4f6e-b2d9-7a5c3e1f4b68","peak":0}]}""",
        ["target.changed"] = """{"from":null,"to":{"profileId":"c:\\games\\game.exe","name":"Game.exe","processId":7},"reason":"detected"}""",
        ["game.detected"] = """{"profileId":"c:\\games\\game.exe","name":"Game.exe","processId":7}""",
        ["game.exited"] = """{"profileId":"c:\\games\\game.exe","name":"Game.exe","processId":7}""",
    };

    private static IEnumerable<string> SnapshotFields => EngineJsonContext.Default.StateSnapshot.Properties.Select(p => p.Name);

    public static TheoryData<string> EventNames => [.. MessageRegistry.Events.Select(e => e.Name)];

    [Theory]
    [MemberData(nameof(EventNames))]
    public void EveryEventMapsToAStateField(string name)
    {
        var type = MessageRegistry.FindEvent(name)!;

        Assert.Contains(type.SnapshotField, SnapshotFields);
        Assert.DoesNotContain(type.SnapshotField, StateSnapshot.IdentityFields);
    }

    [Theory]
    [MemberData(nameof(EventNames))]
    public void EveryEventChangesExactlyItsField(string name)
    {
        var type = MessageRegistry.FindEvent(name)!;
        Assert.True(Samples.TryGetValue(name, out var sample), $"'{name}' has no sample payload in {nameof(MessagesTests)}.");

        var after = type.ApplyJson(Base, JsonDocument.Parse(sample).RootElement);

        var changed = EngineJsonContext.Default.StateSnapshot.Properties
            .Where(p => !Equals(p.Get!(Base), p.Get(after)))
            .Select(p => p.Name);
        Assert.Equal([type.SnapshotField], changed);
    }

    [Fact]
    public void EveryStateFieldHasAnEventThatChangesIt()
    {
        var covered = MessageRegistry.Events.Select(e => e.SnapshotField).ToHashSet();

        Assert.All(SnapshotFields.Except(StateSnapshot.IdentityFields), field => Assert.Contains(field, covered));
    }

    [Fact]
    public void MessageNamesAreUniqueNounVerb()
    {
        var names = MessageRegistry.Commands.Select(c => c.Name).Concat(MessageRegistry.Events.Select(e => e.Name)).Append(EngineMessages.StateSnapshotType).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches(NounVerb(), name));
    }

    [Fact]
    public void WarningEventsRaiseReplaceAndDismiss()
    {
        var replaced = FeedbackMessages.Raised.Apply(Base, ActiveWarning with { Message = "again" });
        Assert.Equal("again", Assert.Single(replaced.Warnings).Message);

        var dismissed = FeedbackMessages.Dismissed.Apply(replaced, new WarningDismissed("w1"));
        Assert.Empty(dismissed.Warnings);
    }

    [Fact]
    public void CaptureStateHasTheSharedShape()
    {
        var state = new CaptureState(ReplayState.Draining, RecordingState.Off, RecordingState.Running, true, new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            """{"replay":"draining","recording":"off","monitorRecording":"running","micMuted":true,"cooldownUntil":"2026-10-25T12:00:00+00:00"}""",
            JsonSerializer.Serialize(state, CaptureJsonContext.Default.CaptureState));
    }

    [Fact]
    public void WarningHasTheSharedShape()
    {
        var json = JsonSerializer.SerializeToElement(ActiveWarning, FeedbackJsonContext.Default.Warning);

        Assert.Equal(
            ["id", "code", "severity", "source", "message", "at", "overlay", "sound", "dismissable"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("error", json.GetProperty("severity").GetString());
    }

    [Fact]
    public void TheInitialStateIsIdleAndMuted()
    {
        var initial = StateSnapshot.Initial("e", 2, SettingsDocument.Empty);

        Assert.Equal(new CaptureState(ReplayState.Off, RecordingState.Off, RecordingState.Off, true, null), initial.Capture);
        Assert.Empty(initial.Warnings);
        Assert.Equal(0, initial.Seq);
    }

    [Fact]
    public void TheHeartbeatCoalescesAndTheSupervisionFieldsAreOptional()
    {
        Assert.True(EngineMessages.Heartbeat.Coalesces);
        Assert.False(EngineMessages.Attached.Coalesces);

        // An engine from before story 8 sends neither field.
        var old = JsonSerializer.Deserialize(
            """{"engineInstanceId":"e","pid":1,"seq":0,"settings":{"schemaVersion":1,"revision":0,"global":{},"profiles":{}},"capture":{"replay":"off","recording":"off","monitorRecording":"off","micMuted":true,"cooldownUntil":null},"warnings":[]}""",
            EngineJsonContext.Default.StateSnapshot)!;
        Assert.Null(old.HeartbeatAt);
        Assert.Null(old.AppPid);
    }

    [Fact]
    public void TheInitialHotkeyStatusListsEveryActionUnbound()
    {
        var initial = StateSnapshot.Initial("e", 2, SettingsDocument.Empty);

        Assert.Equal(Enum.GetValues<HotkeyAction>(), initial.Hotkeys.Bindings.Select(b => b.Action));
        Assert.All(initial.Hotkeys.Bindings, b => Assert.Equal((null, false, ErrorCodes.HotkeyUnbound), (b.Binding, b.Registered, b.Code)));
        Assert.Null(initial.LastHotkeyPress);
        Assert.Equal(new HotkeyStatus([.. initial.Hotkeys.Bindings]), initial.Hotkeys);
    }

    [Fact]
    public void HotkeyMessagesHaveTheSharedShape()
    {
        var status = new HotkeyStatus([new HotkeyBindingStatus(HotkeyAction.RecordToggle, new HotkeyBinding(0x78, HotkeyModifiers.Alt), false, ErrorCodes.HotkeyTaken)]);

        Assert.Equal(
            """{"bindings":[{"action":"recordToggle","binding":{"vk":120,"modifiers":1},"registered":false,"code":"hotkey-taken"}]}""",
            JsonSerializer.Serialize(status, HotkeysJsonContext.Default.HotkeyStatus));
        Assert.Equal(
            """{"action":"save","qpc":42}""",
            JsonSerializer.Serialize(new HotkeyPress(HotkeyAction.Save, 42), HotkeysJsonContext.Default.HotkeyPress));
    }

    [Fact]
    public void ASnapshotFromBeforeHotkeysHasThemUnbound()
    {
        var old = JsonSerializer.Deserialize(
            """{"engineInstanceId":"e","pid":1,"seq":0,"settings":{"schemaVersion":1,"revision":0,"global":{},"profiles":{}},"capture":{"replay":"off","recording":"off","monitorRecording":"off","micMuted":true,"cooldownUntil":null},"warnings":[]}""",
            EngineJsonContext.Default.StateSnapshot)!;

        Assert.Equal(HotkeyStatus.Unbound, old.Hotkeys);
        Assert.Null(old.LastHotkeyPress);
    }

    [Fact]
    public void TheInitialVideoOptionsAreUnprobedWithTheFixedResolutions()
    {
        var initial = StateSnapshot.Initial("e", 2, SettingsDocument.Empty).VideoOptions;

        Assert.False(initial.Probed);
        Assert.Empty(initial.Codecs);
        Assert.Equal(120, initial.MaxFps);
        Assert.Equal(
            [(1280, 720), (1920, 1080), (2560, 1080), (2560, 1440), (3440, 1440), (3840, 1600), (3840, 2160)],
            initial.Resolutions.Select(r => (r.Width, r.Height)));
        Assert.Equal(VideoOptions.Unprobed, new VideoOptions(false, [], [.. VideoOptions.OfferedResolutions], 120));
    }

    [Fact]
    public void VideoOptionsHaveTheirShapeAndAnOldSnapshotReadsAsUnprobed()
    {
        var options = VideoOptions.FromProbe([new CodecOffer(VideoCodec.Hevc, "hevc_nvenc", VideoOptions.MayNotPlayEverywhereFor(VideoCodec.Hevc))]);
        var json = JsonSerializer.Serialize(options, CaptureJsonContext.Default.VideoOptions);

        Assert.StartsWith("""{"probed":true,"codecs":[{"codec":"hevc","encoder":"hevc_nvenc","mayNotPlayEverywhere":true}],"resolutions":[{"width":1280,"height":720},""", json);
        Assert.EndsWith("{\"width\":3840,\"height\":2160}],\"maxFps\":120}", json);
        Assert.False(VideoOptions.MayNotPlayEverywhereFor(VideoCodec.H264));
        Assert.True(VideoOptions.MayNotPlayEverywhereFor(VideoCodec.Av1));

        var old = JsonSerializer.Deserialize(
            """{"engineInstanceId":"e","pid":1,"seq":0,"settings":{"schemaVersion":1,"revision":0,"global":{},"profiles":{}},"capture":{"replay":"off","recording":"off","monitorRecording":"off","micMuted":true,"cooldownUntil":null},"warnings":[]}""",
            EngineJsonContext.Default.StateSnapshot)!;
        Assert.Equal(VideoOptions.Unprobed, old.VideoOptions);
    }

    [Fact]
    public void TheDeviceListHasItsShapeAndAnOldSnapshotReadsAsEmpty()
    {
        var list = new AudioDeviceList([new AudioDevice("{0.0.0.00000000}.{a}", "Speakers", "USB DAC", AudioFlow.Output, false)]);

        Assert.Equal(
            """{"devices":[{"endpointId":"{0.0.0.00000000}.{a}","friendlyName":"Speakers","parentName":"USB DAC","flow":"output","available":false}]}""",
            JsonSerializer.Serialize(list, AudioJsonContext.Default.AudioDeviceList));
        Assert.Equal(list, new AudioDeviceList([.. list.Devices]));
        Assert.Equal(list.GetHashCode(), new AudioDeviceList([.. list.Devices]).GetHashCode());
        Assert.NotEqual(AudioDeviceList.Empty, list);
        Assert.Equal(AudioDeviceList.Empty, StateSnapshot.Initial("e", 2, SettingsDocument.Empty).Devices);

        var old = JsonSerializer.Deserialize(
            """{"engineInstanceId":"e","pid":1,"seq":0,"settings":{"schemaVersion":1,"revision":0,"global":{},"profiles":{}},"capture":{"replay":"off","recording":"off","monitorRecording":"off","micMuted":true,"cooldownUntil":null},"warnings":[]}""",
            EngineJsonContext.Default.StateSnapshot)!;
        Assert.Equal(AudioDeviceList.Empty, old.Devices);
    }

    [Fact]
    public void TargetChangedHasItsShapeAndAnOldSnapshotHasNoTarget()
    {
        var changed = new TargetChanged(new TargetGame(@"c:\games\a.exe", "A.exe", 3), null, TargetChangeReason.ColdStart);

        Assert.Equal(
            """{"from":{"profileId":"c:\\games\\a.exe","name":"A.exe","processId":3},"to":null,"reason":"cold-start"}""",
            JsonSerializer.Serialize(changed, GameJsonContext.Default.TargetChanged));
        Assert.Null(GameMessages.TargetChanged.Apply(Base with { TargetGame = changed.From }, changed).TargetGame);

        var old = JsonSerializer.Deserialize(
            """{"engineInstanceId":"e","pid":1,"seq":0,"settings":{"schemaVersion":1,"revision":0,"global":{},"profiles":{}},"capture":{"replay":"off","recording":"off","monitorRecording":"off","micMuted":true,"cooldownUntil":null},"warnings":[]}""",
            EngineJsonContext.Default.StateSnapshot)!;
        Assert.Null(old.TargetGame);
    }

    [Theory]
    [InlineData("""{"pid":5}""", null)]
    [InlineData("""{"pid":5,"engineRestart":"crashed"}""", EngineRestartReason.Crashed)]
    public void AppAttachCarriesAnOptionalRestartReason(string json, EngineRestartReason? reason)
    {
        var attach = JsonSerializer.Deserialize(json, EngineJsonContext.Default.AppAttach)!;

        Assert.Equal(new AppAttach(5, reason), attach);
    }

    [GeneratedRegex("^[a-z]+\\.[a-z][a-zA-Z]*$")]
    private static partial Regex NounVerb();
}

public sealed class FrameTests
{
    [Fact]
    public void AnEventFrameIsOneLineWithTheEnvelope()
    {
        var frame = FrameWriter.Event("warning.dismissed", 42, new WarningDismissed("x"), FeedbackJsonContext.Default.WarningDismissed);

        Assert.Equal("""{"v":1,"type":"warning.dismissed","seq":42,"payload":{"id":"x"}}""" + "\n", Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public void CommandAndErrorFramesCarryTheirId()
    {
        Assert.Equal(
            """{"v":1,"id":"7","type":"engine.shutdown","payload":{}}""" + "\n",
            Encoding.UTF8.GetString(FrameWriter.Command("7", "engine.shutdown", Empty.Value, EngineJsonContext.Default.Empty)));
        Assert.Equal(
            """{"v":1,"id":"7","type":"x.y","error":{"code":"command-unknown","message":"no"}}""" + "\n",
            Encoding.UTF8.GetString(FrameWriter.Error("7", "x.y", ErrorCodes.CommandUnknown, "no")));
    }

    [Fact]
    public async Task FramesAreReadBackAcrossChunksAndBlankLines()
    {
        var bytes = Encoding.UTF8.GetBytes("\n\r\n")
            .Concat(FrameWriter.Event("a.b", 1, Empty.Value, EngineJsonContext.Default.Empty))
            .Concat(FrameWriter.Error("9", "c.d", "e-f", "g"))
            .ToArray();
        await using var reader = new FrameReader(new TrickleStream(bytes), 1024);

        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var end = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("a.b", 1L, (string?)null), (first!.Type, first.Seq!.Value, first.Id));
        Assert.Equal(("9", "e-f"), (second!.Id, second.Error!.Code));
        Assert.Null(end);
    }

    [Theory]
    [InlineData("not json\n")]
    [InlineData("{\"v\":1,\"payload\":{}}\n")]
    public async Task AFrameThatIsNotAnEnvelopeIsInvalidData(string text)
    {
        await using var reader = new FrameReader(new MemoryStream(Encoding.UTF8.GetBytes(text)), 1024);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnOverlongFrameIsInvalidDataEvenWithoutANewline()
    {
        await using var reader = new FrameReader(new MemoryStream(new byte[5000]), 1024);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APartialLastLineIsTheEndOfTheStream()
    {
        await using var reader = new FrameReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"a.b\"")), 1024);

        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThePipeNameCarriesInstanceAndUser()
    {
        Assert.Equal("ReplayBuffer.Engine.v1.ReplayBuffer-Dev.S-1-5-21-1", PipeName.For("ReplayBuffer-Dev", "S-1-5-21-1"));
        Assert.EndsWith("." + PipeName.CurrentUserSid.Value, PipeName.ForCurrentUser("ReplayBuffer-Dev"));
    }

    /// <summary>Hands out three bytes per read, to split frames across reads.</summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
