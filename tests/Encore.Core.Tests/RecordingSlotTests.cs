using Encore.Core.Sessions;

namespace Encore.Core.Tests;

public sealed class RecordingSlotTests
{
    [Fact]
    public void ARestartMovesTheRunningRecordingToEndingAndItsReplacementFollowsIt()
    {
        var slot = new RecordingSlot();
        var first = new RecordingRun(1, "first.mp4", new NoWrite());
        slot.Start(first);
        Assert.False(slot.IsOff);

        Assert.True(slot.TryMoveRunningToEnding());
        var second = new RecordingRun(1, "second.mp4", new NoWrite()) { Follows = first };
        slot.Start(second);

        // A second restart waits for the first one's Recording to finish.
        Assert.False(slot.TryMoveRunningToEnding());
        Assert.Same(second, slot.ReplacementOf(first));

        Assert.Equal(RecordingPlace.Ending, slot.Remove(first));
        Assert.Equal(RecordingPlace.None, slot.Remove(first));
        Assert.False(slot.IsOff);
        Assert.Equal(RecordingPlace.Running, slot.Remove(second));
        Assert.True(slot.IsOff);
    }

    private sealed class NoWrite : IRecordingWrite
    {
        public void Start(string path, Action<Exception?> done)
        {
        }

        public void Stop(long qpc)
        {
        }
    }
}
