namespace Encore.Media.Tests;

public sealed class BoundedHandoffTests
{
    [Fact]
    public void ItemsComeOutInOrderAndAFullQueueRefusesMore()
    {
        var queue = new BoundedHandoff<long>(2);

        Assert.True(queue.TryAdd(1));
        Assert.True(queue.TryAdd(2));
        Assert.True(queue.IsFull);
        Assert.False(queue.TryAdd(3));

        Assert.True(queue.TryTake(out var first));
        Assert.True(queue.TryAdd(4));
        Assert.True(queue.TryTake(out var second));
        Assert.True(queue.TryTake(out var third));
        Assert.Equal([1L, 2L, 4L], [first, second, third]);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CompletingLetsTheConsumerDrainThenStop()
    {
        var queue = new BoundedHandoff<int>(4);
        queue.TryAdd(7);
        queue.Complete();

        Assert.False(queue.TryAdd(8));
        Assert.True(queue.TryTake(out var item));
        Assert.Equal(7, item);
        Assert.False(queue.TryTake(out _));
    }

    [Fact]
    public void TakingNowNeverWaitsAndTellsADrainedQueueFromAnEmptyOne()
    {
        var queue = new BoundedHandoff<int>(2);
        Assert.False(queue.TryTakeNow(out _, out var drained));
        Assert.False(drained);

        queue.TryAdd(5);
        queue.Complete();
        Assert.True(queue.TryTakeNow(out var item, out drained));
        Assert.Equal(5, item);
        Assert.False(drained);
        Assert.False(queue.TryTakeNow(out _, out drained));
        Assert.True(drained);
    }

    [Fact]
    public void AWaitingConsumerWakesForAnItemAndForCompletion()
    {
        var queue = new BoundedHandoff<int>(3);
        var taken = new List<int>();
        var consumer = new Thread(() =>
        {
            while (queue.TryTake(out var item))
            {
                taken.Add(item);
            }
        });
        consumer.Start();

        for (var i = 0; i < 1000; i++)
        {
            while (!queue.TryAdd(i))
            {
                Thread.Yield();
            }
        }

        queue.Complete();
        Assert.True(consumer.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(Enumerable.Range(0, 1000), taken);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AddingAndTakingDoNotAllocate()
    {
        var queue = new BoundedHandoff<long>(3);
        queue.TryAdd(0);
        queue.TryTake(out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            queue.TryAdd(i);
            queue.TryTake(out _);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
