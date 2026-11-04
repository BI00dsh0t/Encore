using System.Runtime.InteropServices;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Feedback;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

public sealed class FeedbackSoundsTests
{
    private const string DefaultEndpoint = "{0.0.0.00000000}.{comms}";

    private static readonly CaptureState Running = CaptureState.Initial with { Recording = RecordingState.Running };

    [Fact]
    public void EachOutcomePlaysItsSoundOnceOnTheDefaultCommunicationsEndpoint()
    {
        var output = new FakeOutput();
        var notices = new Notices();
        var player = new FeedbackSoundPlayer(output, () => SettingsDocument.Empty, (_, _) => { });
        using (var router = new FeedbackRouter(notices, TimeProvider.System, player))
        {
            router.ClipSaved(new ClipSaved(@"c:\clips\a.mp4"));
            router.ClipNotSaved(new ClipFailed(ErrorCodes.SaveFailed));
            router.RecordingStarted(new RecordingStarted(@"c:\clips\a-rec.mp4"));
            router.RecordingStopped(new RecordingStopped(@"c:\clips\a-rec.mp4", RecordingStopReasons.Stopped));
            router.DiskFull();
        }

        player.Dispose();

        FeedbackSound[] all = [FeedbackSound.ClipSaved, FeedbackSound.SaveFailed, FeedbackSound.RecordingStarted, FeedbackSound.RecordingStopped, FeedbackSound.DiskFull];
        Assert.Equal(all.Select(s => (DefaultEndpoint, (FeedbackSound?)s)), output.Played);
        Assert.Equal(["clipSaved", "saveFailed", "recordingStarted", "recordingStopped", "diskFull"], notices.Raised.Select(n => n.Sound));
    }

    [Fact]
    public void EachRecordingStopNamesItsReasonAndADiskFullOrRestartPlaysOneSound()
    {
        const string File = @"c:\clips\Game\a.mp4";
        var output = new FakeOutput();
        var notices = new Notices();
        var player = new FeedbackSoundPlayer(output, () => SettingsDocument.Empty, (_, _) => { });
        string[] plain = [RecordingStopReasons.Stopped, RecordingStopReasons.GameExit, RecordingStopReasons.Ignored, RecordingStopReasons.SessionEnded, RecordingStopReasons.Shutdown, ErrorCodes.EncodeFailed];
        using (var router = new FeedbackRouter(notices, TimeProvider.System, player))
        {
            router.RecordingStarted(new RecordingStarted(File));
            foreach (var reason in plain)
            {
                router.RecordingStopped(new RecordingStopped(File, reason));
            }

            // A disk-full stop, then its own notice.
            router.StateChanged(CaptureState.Initial);
            router.RecordingStopped(new RecordingStopped(File, RecordingStopReasons.DiskFull));
            router.DiskFull();

            // An encoder restart with a replacement: the state stays Running throughout.
            router.StateChanged(Running);
            router.RecordingStopped(new RecordingStopped(File, RecordingStopReasons.EncoderRestart));
            router.RecordingStarted(new RecordingStarted(@"c:\clips\Game\b.mp4"));
            router.RecordingRestarted(new RecordingRestart(File, @"c:\clips\Game\b.mp4"));
        }

        player.Dispose();

        FeedbackSound[] expected = [FeedbackSound.RecordingStarted, .. plain.Select(_ => FeedbackSound.RecordingStopped), FeedbackSound.DiskFull, FeedbackSound.RecordingStarted];
        Assert.Equal(expected.Select(s => (DefaultEndpoint, (FeedbackSound?)s)), output.Played);
        Assert.Equal(
            [ErrorCodes.RecordingStarted, .. plain.Select(_ => ErrorCodes.RecordingStopped), ErrorCodes.RecordingStopped, ErrorCodes.DiskFull, ErrorCodes.RecordingStopped, ErrorCodes.RecordingRestarted],
            notices.Raised.Select(n => n.Code));
        var stops = notices.Raised.Where(n => n.Code == ErrorCodes.RecordingStopped).ToList();
        Assert.Equal(
            ["Recording stopped", "Recording stopped: the game exited", "Recording stopped: the game was set Ignored", "Recording stopped: the game session ended", "Recording stopped: ReplayBuffer shut down", $"Recording stopped: the write failed ({ErrorCodes.EncodeFailed})", "Recording stopped: the disk is full", "Recording stopped: encoder restart"],
            stops.Select(n => n.Message));
        Assert.Equal(WarningSeverity.Warning, stops[5].Severity);
        Assert.Null(stops[6].Sound);
        Assert.Null(stops[7].Sound);
    }

    [Fact]
    public void ASwitchedOffSoundDoesNotPlayAndItsNoticeHasNoSoundButAPreviewPlaysIt()
    {
        var output = new FakeOutput();
        var notices = new Notices();
        var off = Keys.FeedbackSounds.Default with { ClipSaved = false };
        var document = SettingsDocument.Empty with { Global = SettingsMaps.EmptyValues.Add(Keys.FeedbackSounds.Name, Keys.FeedbackSounds.ToJson(off)) };
        var player = new FeedbackSoundPlayer(output, () => document, (_, _) => { });
        using (var router = new FeedbackRouter(notices, TimeProvider.System, player))
        {
            router.ClipSaved(new ClipSaved(@"c:\clips\a.mp4"));
        }

        Assert.Null(Assert.Single(notices.Raised).Sound);

        player.Preview(FeedbackSound.ClipSaved);
        player.Dispose();

        Assert.Equal((DefaultEndpoint, (FeedbackSound?)FeedbackSound.ClipSaved), Assert.Single(output.Played));
    }

    [Fact]
    public void AFailingPlayIsReportedAndSkippedAndTheNextSoundPlays()
    {
        var output = new FakeOutput { FailNext = true };
        var failures = new List<Exception>();
        var player = new FeedbackSoundPlayer(output, () => SettingsDocument.Empty, (_, e) => failures.Add(e));

        Assert.True(player.Play("clipSaved"));
        Assert.True(player.Play("diskFull"));
        player.Dispose();

        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal((DefaultEndpoint, (FeedbackSound?)FeedbackSound.DiskFull), Assert.Single(output.Played));
    }

    [Fact]
    public void OnlyATrackMixingTheSoundEndpointAsAnOutputDeviceCaptures()
    {
        var game = new LayoutDevice(Guid.NewGuid(), DefaultEndpoint, "Headset", "", AudioFlow.Output, IsMic: false);
        var sameIdInput = new LayoutDevice(Guid.NewGuid(), DefaultEndpoint, "Headset mic", "", AudioFlow.Input, IsMic: true);
        var speakers = new LayoutDevice(Guid.NewGuid(), "{0.0.0.00000000}.{speakers}", "Speakers", "", AudioFlow.Output, IsMic: false);
        var gameTrack = new Track(Guid.NewGuid(), "Game", [speakers.Key, game.Key], MuteAffected: false);
        var micTrack = new Track(Guid.NewGuid(), "Mic", [sameIdInput.Key], MuteAffected: true);
        var speakerTrack = new Track(Guid.NewGuid(), "Speakers", [speakers.Key], MuteAffected: false);

        var captured = FeedbackCapture.CapturingTracks(new TrackLayout([game, sameIdInput, speakers], [gameTrack, micTrack, speakerTrack]), DefaultEndpoint);
        var unused = FeedbackCapture.CapturingTracks(new TrackLayout([game, sameIdInput, speakers], [micTrack, speakerTrack]), DefaultEndpoint);

        Assert.Equal((gameTrack, game), Assert.Single(captured));
        Assert.Empty(unused);
    }

    [Fact]
    public void EachSoundIsShortPeaksAtMinusTwelveAndTheChimeRisesWhileTheBuzzFalls()
    {
        foreach (var sound in Enum.GetValues<FeedbackSound>())
        {
            var samples = FeedbackSoundSynth.Render(sound).Span;
            var frames = samples.Length / FeedbackSoundSynth.Channels;
            Assert.InRange(frames, 1, (FeedbackSoundSynth.SampleRate * 600 / 1000) - 1);
            var peak = 0f;
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            Assert.InRange(20 * Math.Log10(peak), -12.1, -11.9);
        }

        var chime = Crossings(FeedbackSound.ClipSaved);
        var buzz = Crossings(FeedbackSound.SaveFailed);
        Assert.True(chime.Last > chime.First, $"clipSaved: {chime}");
        Assert.True(buzz.Last < buzz.First, $"saveFailed: {buzz}");
    }

    /// <summary>The zero crossings of the left channel over the first and the last 100 ms.</summary>
    private static (int First, int Last) Crossings(FeedbackSound sound)
    {
        var samples = FeedbackSoundSynth.Render(sound).Span;
        var frames = samples.Length / FeedbackSoundSynth.Channels;
        var window = FeedbackSoundSynth.SampleRate / 10;
        return (Count(samples, 0, window), Count(samples, frames - window, frames));
    }

    private static int Count(ReadOnlySpan<float> samples, int fromFrame, int toFrame)
    {
        var count = 0;
        for (var i = fromFrame + 1; i < toFrame; i++)
        {
            var (a, b) = (samples[FeedbackSoundSynth.Channels * (i - 1)], samples[FeedbackSoundSynth.Channels * i]);
            if ((a < 0 && b >= 0) || (a >= 0 && b < 0))
            {
                count++;
            }
        }

        return count;
    }

    private sealed class FakeOutput : IFeedbackSoundOutput
    {
        private readonly Lock _lock = new();
        private readonly List<(string, FeedbackSound?)> _played = [];

        public bool FailNext { get; set; }

        public IReadOnlyList<(string Endpoint, FeedbackSound? Sound)> Played
        {
            get
            {
                lock (_lock)
                {
                    return [.. _played];
                }
            }
        }

        public string? DefaultEndpointId() => DefaultEndpoint;

        public void Play(string endpointId, ReadOnlyMemory<float> pcm)
        {
            lock (_lock)
            {
                if (FailNext)
                {
                    FailNext = false;
                    throw new InvalidOperationException("The device is gone.");
                }

                _played.Add((endpointId, Identify(pcm)));
            }
        }

        /// <summary>The sound whose cached samples <paramref name="pcm"/> is.</summary>
        private static FeedbackSound? Identify(ReadOnlyMemory<float> pcm)
        {
            MemoryMarshal.TryGetArray(pcm, out var played);
            foreach (var sound in Enum.GetValues<FeedbackSound>())
            {
                MemoryMarshal.TryGetArray(FeedbackSoundSynth.Render(sound), out var rendered);
                if (ReferenceEquals(played.Array, rendered.Array))
                {
                    return sound;
                }
            }

            return null;
        }
    }

    private sealed class Notices : IFeedbackNotices
    {
        public List<FeedbackNotice> Raised { get; } = [];

        public string Raise(FeedbackNotice notice)
        {
            Raised.Add(notice);
            return Guid.NewGuid().ToString("N");
        }

        public void Withdraw(string id)
        {
        }
    }
}
