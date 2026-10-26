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
        ["warning.raised"] = """{"id":"w2","code":"settings-unreadable","severity":"warning","source":"settings","message":"m","at":"2026-10-25T12:00:00+00:00","overlay":true,"sound":null,"dismissable":true}""",
        ["warning.dismissed"] = """{"id":"w1"}""",
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
    [InlineData("[1,2]\n")]
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
