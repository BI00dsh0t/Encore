using System.Collections.Concurrent;
using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

/// <summary>The wait for a Target window that starts as a tiny placeholder (bugfix tiny-startup-window-capture).</summary>
public sealed partial class SessionOrchestratorTests
{
    [Fact]
    public async Task ATinyStartupWindowIsWaitedForAndAFirstFrameTimeoutIsRetriedUntilASaveWorks()
    {
        var folder = Directory.CreateTempSubdirectory("rb-wait-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
            var waiting = System.Threading.Channels.Channel.CreateUnbounded<string>();
            var ended = System.Threading.Channels.Channel.CreateUnbounded<bool>();
            var failures = new ConcurrentQueue<Exception>();
            var writes = new ConcurrentQueue<string>();
            var factory = new FakeFactory { Save = _ => new FakeWrite(writes) };

            // Rocket League's placeholder window, until the game grows it.
            var size = "156x27";
            var wait = new CaptureWait(
                _ => Volatile.Read(ref size) == "156x27" ? "the window is 156x27, under 320x180" : null,
                (_, why) => waiting.Writer.TryWrite(why),
                (_, captured) => ended.Writer.TryWrite(captured))
            {
                Poll = TimeSpan.FromMilliseconds(20),
                FirstRetry = TimeSpan.FromMilliseconds(50),
            };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                targetCapture: new TargetCapture(_ => NoAudio, (_, e) => failures.Enqueue(e), wait),
                clips: new ReplayClips(OutputPathAllocator.System, () => folder, s => saved.Writer.TryWrite(s), failures.Enqueue));

            // Detected while tiny: no session starts on the placeholder.
            orchestrator.Post(new ProposeTarget(new TargetCandidate(Pid, 1, ProfileId, "RocketLeague.exe"), TargetChangeReason.Detected));
            Assert.Equal("the window is 156x27, under 320x180", await NextAsync(waiting));
            Assert.Empty(factory.Sessions);

            // It grows: the poll starts the session, whose first frame never arrives.
            Volatile.Write(ref size, "1920x1080");
            var first = await SessionNumber(factory, 1);
            first.Events.Failed(new ErrorCodeException(ErrorCodes.CaptureFailed, "No frame arrived from the window within 5 s."));

            // The failure isn't final: a retry starts a new session, and its first frame ends the wait.
            var second = await SessionNumber(factory, 2);
            second.Events.Anchored(Stopwatch.GetTimestamp());
            Assert.True(await NextAsync(ended));

            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp()));
            var clip = await NextAsync(saved);

            Assert.Equal([clip.Path], writes);
            Assert.StartsWith("failure:", first.EndReason, StringComparison.Ordinal);
            Assert.Null(second.EndReason);
            Assert.False(waiting.Reader.TryRead(out _));
            Assert.Empty(failures);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The factory's session <paramref name="number"/>, once it has started.</summary>
    private static async Task<FakeSession> SessionNumber(FakeFactory factory, int number)
    {
        var deadline = Stopwatch.StartNew();
        while (factory.Sessions.Count < number)
        {
            Assert.True(deadline.Elapsed < Timeout, $"session {number} never started");
            await Task.Delay(10, Token);
        }

        return factory.Sessions[number - 1];
    }
}
