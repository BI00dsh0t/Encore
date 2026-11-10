namespace Encore.Contracts.Tests;

public sealed class SupervisionTests
{
    private static readonly TimeSpan[] Expected = [.. new[] { 0, 1, 2, 4, 8, 16, 30, 30, 30 }.Select(s => TimeSpan.FromSeconds(s))];

    [Fact]
    public void TheTimingsAreTheSpines()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), Supervision.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(6), Supervision.SilenceLimit);
        Assert.Equal(TimeSpan.FromSeconds(10), Supervision.ReplyLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), Supervision.BackoffReset);
    }

    [Fact]
    public void RestartsInARowBackOffTo30Seconds()
    {
        var time = new ManualTime();
        var backoff = new RestartBackoff(time);

        var delays = new List<TimeSpan>();
        foreach (var _ in Expected)
        {
            var delay = backoff.Next();
            delays.Add(delay);

            // The restart happens after its delay, and fails again at once.
            time.Advance(delay);
        }

        Assert.Equal(Expected, delays);
    }

    [Fact]
    public void SixtySecondsWithNoRestartStartsTheBackoffAgain()
    {
        var time = new ManualTime();
        var backoff = new RestartBackoff(time);
        for (var i = 0; i < 4; i++)
        {
            time.Advance(backoff.Next());
        }

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.Next());

        time.Advance(TimeSpan.FromSeconds(8) + TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }

    [Fact]
    public void ThreeRestartsInTenMinutesAreAllowedAndTheFourthIsACrashLoopUntilTheWindowSlides()
    {
        var time = new ManualTime();
        var guard = new CrashLoopGuard(time);
        Assert.True(guard.TryRestart());
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(guard.TryRestart());
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(guard.TryRestart());

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(guard.TryRestart());

        // 10 min 15 s after the first restart, it has left the window; the other two haven't.
        time.Advance(TimeSpan.FromSeconds(525));
        Assert.True(guard.TryRestart());
        Assert.False(guard.TryRestart());
    }

    [Fact]
    public void AResetGuardAllowsRestartsAgain()
    {
        var guard = new CrashLoopGuard(new ManualTime());
        for (var i = 0; i < Supervision.CrashLoopRestarts; i++)
        {
            Assert.True(guard.TryRestart());
        }

        Assert.False(guard.TryRestart());
        guard.Reset();
        Assert.True(guard.TryRestart());
    }

    [Fact]
    public void TheMutexNameCarriesProcessAndInstance()
    {
        Assert.Equal(@"Local\Encore.Engine.v1.Encore-Dev", SingleInstance.MutexName(SingleInstance.Engine, Instance.DevName));
        Assert.Equal(@"Local\Encore.App.v1.Encore", SingleInstance.MutexName(SingleInstance.App, Instance.InstalledName));
    }

    [Fact]
    public void AHeldMutexRefusesASecondCopyUntilItIsFreed()
    {
        var instance = TestInstance();
        using var release = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        SingleInstance? first = null;
        var holder = new Thread(() =>
        {
            first = SingleInstance.TryAcquire(SingleInstance.App, instance);
            acquired.Set();
            release.Wait();
            first?.Dispose();
        });
        holder.Start();
        acquired.Wait(TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        Assert.Null(SingleInstance.TryAcquire(SingleInstance.App, instance));
        using (var otherProcess = SingleInstance.TryAcquire(SingleInstance.Engine, instance))
        {
            Assert.NotNull(otherProcess);
        }

        release.Set();
        holder.Join();

        using var second = SingleInstance.TryAcquire(SingleInstance.App, instance);
        Assert.NotNull(second);
    }

    [Fact]
    public void AMutexAbandonedByAThreadThatExitedHoldingItCountsAsAcquired()
    {
        var instance = TestInstance();
        SingleInstance? abandoned = null;
        var holder = new Thread(() => abandoned = SingleInstance.TryAcquire(SingleInstance.Engine, instance));
        holder.Start();
        holder.Join();
        Assert.NotNull(abandoned);

        try
        {
            // Join returns when the managed thread is done; Windows abandons the mutex only once the OS
            // thread has ended, a moment later on a loaded machine, so the take is retried briefly.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var taken = SingleInstance.TryAcquire(SingleInstance.Engine, instance);
            while (taken is null && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(20);
                taken = SingleInstance.TryAcquire(SingleInstance.Engine, instance);
            }

            using (taken)
            {
                Assert.NotNull(taken);
            }
        }
        finally
        {
            abandoned!.Dispose();
        }
    }

    private static string TestInstance() => Instance.TestPrefix + Guid.NewGuid().ToString("N");

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
