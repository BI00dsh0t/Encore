using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Engine.Audio;
using ReplayBuffer.Engine.Ipc;

namespace ReplayBuffer.Engine.Tests;

/// <summary>Each dropout the hub reports is one <c>device-dropout</c> warning; a resume raises and dismisses nothing.</summary>
public sealed class DeviceDropoutReporterTests
{
    [Fact]
    public void EachDropoutRaisesOneWarningAndAResumeNone()
    {
        var hub = new EventHub(SettingsDocument.Empty);
        var source = new FakeDropoutSource();
        using var reporter = new DeviceDropoutReporter(source, new Warnings(hub, TimeProvider.System));
        var game = new AudioDeviceDropout("{0.0.0.00000000}.{a}", AudioFlow.Output, "Game", "BEACN");

        source.Drop(game);
        source.Resume(game);
        source.Drop(game);

        var warnings = hub.State.Warnings;
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w =>
        {
            Assert.Equal(ErrorCodes.DeviceDropout, w.Code);
            Assert.Equal(WarningSeverity.Warning, w.Severity);
            Assert.Equal("audio", w.Source);
            Assert.False(w.Overlay);
            Assert.True(w.Dismissable);
            Assert.Contains("'Game'", w.Message, StringComparison.Ordinal);

            // Story 10.11: the glossary's words, the code unchanged.
            Assert.EndsWith("was disconnected. Your audio tracks record silence from it until it's back.", w.Message, StringComparison.Ordinal);
        });
        Assert.NotEqual(warnings[0].Id, warnings[1].Id);
    }

    [Fact]
    public void ADisposedReporterRaisesNothing()
    {
        var hub = new EventHub(SettingsDocument.Empty);
        var source = new FakeDropoutSource();
        var reporter = new DeviceDropoutReporter(source, new Warnings(hub, TimeProvider.System));

        reporter.Dispose();
        source.Drop(new AudioDeviceDropout("{0.0.0.00000000}.{a}", AudioFlow.Input, "Mic", "BEACN"));

        Assert.Empty(hub.State.Warnings);
        Assert.False(source.HasHandlers);
    }

    private sealed class FakeDropoutSource : IAudioDropoutSource
    {
        public event Action<AudioDeviceDropout>? Dropped;

        public event Action<AudioDeviceDropout>? Resumed;

        public bool HasHandlers => Dropped is not null || Resumed is not null;

        public void Drop(AudioDeviceDropout dropout) => Dropped?.Invoke(dropout);

        public void Resume(AudioDeviceDropout dropout) => Resumed?.Invoke(dropout);
    }
}
