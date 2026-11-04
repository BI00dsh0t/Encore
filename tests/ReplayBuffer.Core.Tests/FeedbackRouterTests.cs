using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Feedback;

namespace ReplayBuffer.Core.Tests;

public sealed class FeedbackRouterTests
{
    [Fact]
    public void ASavedClipRaisesOneNoticeThatIsWithdrawnTenSecondsLater()
    {
        var notices = new FakeNotices();
        var time = new ManualTime();
        using var router = new FeedbackRouter(notices, time);

        router.ClipSaved(new ClipSaved(@"c:\clips\a.mp4"));

        var notice = Assert.Single(notices.Active).Value;
        Assert.Equal(new FeedbackNotice(ErrorCodes.ClipSaved, WarningSeverity.Info, "clips", "Clip saved", "clipSaved"), notice);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Single(notices.Active);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(notices.Active);
    }

    [Fact]
    public void AFailedSaveStaysUntilTheNextSavedClip()
    {
        var notices = new FakeNotices();
        var time = new ManualTime();
        using var router = new FeedbackRouter(notices, time);

        router.ClipNotSaved(new ClipFailed(ErrorCodes.DiskFull));

        var failed = Assert.Single(notices.Active).Value;
        Assert.Equal(ErrorCodes.ClipFailed, failed.Code);
        Assert.Equal(WarningSeverity.Error, failed.Severity);
        Assert.Equal("Clip not saved: disk full", failed.Message);
        Assert.Equal("saveFailed", failed.Sound);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(notices.Active);

        router.ClipSaved(new ClipSaved(@"c:\clips\b.mp4"));

        Assert.Equal(ErrorCodes.ClipSaved, Assert.Single(notices.Active).Value.Code);
    }

    private sealed class FakeNotices : IFeedbackNotices
    {
        public Dictionary<string, FeedbackNotice> Active { get; } = [];

        public string Raise(FeedbackNotice notice)
        {
            var id = Guid.NewGuid().ToString("N");
            Active[id] = notice;
            return id;
        }

        public void Withdraw(string id) => Active.Remove(id);
    }

    /// <summary>A clock that moves only when told, firing the one-shot timers that come due.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var due in _timers.Where(t => t.DueAt <= _now).ToList())
            {
                _timers.Remove(due);
                due.Fire();
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            public DateTimeOffset DueAt { get; } = dueAt;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => owner._timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
