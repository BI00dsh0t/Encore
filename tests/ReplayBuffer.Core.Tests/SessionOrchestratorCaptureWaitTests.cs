using System.Collections.Concurrent;
using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

/// <summary>The game session follows the Target's process across its windows (bugfix tiny-startup-window-capture).</summary>
public sealed partial class SessionOrchestratorTests
{
    private const string Tiny = "the window is 156x27, under 320x180";

    [Fact]
    public async Task ATinyStartupWindowIsWaitedForRetriedAndFollowedToTheProcesssNextWindowKeepingTheRing()
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

            // The process's windows, and whether each can be captured: Rocket League's placeholder first.
            var windows = new ConcurrentDictionary<nint, bool> { [1] = false };
            var wait = new CaptureWait(
                target => windows.TryGetValue(target.Window, out var ok) && ok ? CaptureWindow.Of(target.Window)
                    : windows.FirstOrDefault(w => w.Value) is { Value: true } other ? CaptureWindow.Of(other.Key)
                    : CaptureWindow.None(Tiny),
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
            Assert.Equal(Tiny, await NextAsync(waiting));
            Assert.Empty(factory.Sessions);

            // It grows: the poll starts the session, whose first frame never arrives.
            windows[1] = true;
            var first = await SessionNumber(factory, 1);
            first.Events.Failed(new ErrorCodeException(ErrorCodes.CaptureFailed, "No frame arrived from the window within 5 s."));

            // The failure isn't final: a retry starts a new session, and its first frame ends the wait.
            var second = await SessionNumber(factory, 2);
            second.Events.Anchored(Stopwatch.GetTimestamp());
            Assert.True(await NextAsync(ended));
            var press = Stopwatch.GetTimestamp();
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press));
            var before = await NextAsync(saved);

            // The placeholder is destroyed and the game shows its real window, a new handle: the
            // session moves to it rather than ending, so its ring is kept.
            windows.TryRemove(1, out _);
            windows[2] = true;
            second.Events.Closed("window closed");
            await Until(() => second.Retargets.Contains(2));
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (2 * Stopwatch.Frequency)));
            var after = await NextAsync(saved);

            Assert.Equal([before.Path, after.Path], writes);
            Assert.Equal(2, factory.Sessions.Count);
            Assert.StartsWith("failure:", first.EndReason, StringComparison.Ordinal);
            Assert.Null(second.EndReason);
            Assert.Equal([(nint)2], second.Retargets);
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
        await Until(() => factory.Sessions.Count >= number);
        return factory.Sessions[number - 1];
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < Timeout, "the condition never held");
            await Task.Delay(10, Token);
        }
    }
}
