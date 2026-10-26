using ReplayBuffer.Engine.Ipc;

namespace ReplayBuffer.Engine.Tests;

public sealed class WriterQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACoalescingFrameReplacesItsQueuedValueAtTheTail()
    {
        var queue = new WriterQueue(10, TimeProvider.System);

        queue.TryEnqueue([1], "a");
        queue.TryEnqueue([2]);
        queue.TryEnqueue([3], "a");

        Assert.Equal(2, queue.Count);
        Assert.Equal([2], await queue.DequeueAsync(Token));
        Assert.Equal([3], await queue.DequeueAsync(Token));
    }

    [Fact]
    public async Task ADequeuedCoalescingFrameIsNotReplacedByTheNextOne()
    {
        var queue = new WriterQueue(10, TimeProvider.System);
        queue.TryEnqueue([1], "a");
        Assert.Equal([1], await queue.DequeueAsync(Token));

        queue.TryEnqueue([2], "a");

        Assert.Equal([2], await queue.DequeueAsync(Token));
    }

    [Fact]
    public async Task GoingPastTheLimitClosesTheQueue()
    {
        var queue = new WriterQueue(2, TimeProvider.System);

        Assert.True(queue.TryEnqueue([1]));
        Assert.True(queue.TryEnqueue([2]));
        Assert.False(queue.TryEnqueue([3]));
        Assert.False(queue.TryEnqueue([4]));
        Assert.Null(await queue.DequeueAsync(Token));
    }

    [Fact]
    public async Task ACoalescingFrameAtTheLimitStillReplacesItsValue()
    {
        var queue = new WriterQueue(2, TimeProvider.System);
        queue.TryEnqueue([1]);
        queue.TryEnqueue([2], "a");

        Assert.True(queue.TryEnqueue([3], "a"));
        Assert.Equal([1], await queue.DequeueAsync(Token));
        Assert.Equal([3], await queue.DequeueAsync(Token));
    }

    [Fact]
    public async Task LagIsTheAgeOfTheOldestUnwrittenFrameIncludingTheOneInFlight()
    {
        var time = new ManualTime();
        var queue = new WriterQueue(10, time);
        Assert.Equal(TimeSpan.Zero, queue.Lag);

        queue.TryEnqueue([1]);
        time.Advance(TimeSpan.FromSeconds(2));
        queue.TryEnqueue([2]);
        await queue.DequeueAsync(Token);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), queue.Lag);

        queue.Written();
        Assert.Equal(TimeSpan.FromSeconds(4), queue.Lag);
    }

    [Fact]
    public async Task ADequeueWaitsForAFrame()
    {
        var queue = new WriterQueue(10, TimeProvider.System);
        var next = queue.DequeueAsync(Token).AsTask();
        Assert.False(next.IsCompleted);

        queue.TryEnqueue([9]);

        Assert.Equal([9], await next.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task CompleteHandsOutWhatIsQueuedThenEnds()
    {
        var queue = new WriterQueue(10, TimeProvider.System);
        queue.TryEnqueue([1]);

        queue.Complete();

        Assert.False(queue.TryEnqueue([2]));
        Assert.Equal([1], await queue.DequeueAsync(Token));
        Assert.Null(await queue.DequeueAsync(Token));
    }

    [Fact]
    public async Task CloseEndsAWaitingDequeue()
    {
        var queue = new WriterQueue(10, TimeProvider.System);
        var next = queue.DequeueAsync(Token).AsTask();

        queue.Close();

        Assert.Null(await next.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
