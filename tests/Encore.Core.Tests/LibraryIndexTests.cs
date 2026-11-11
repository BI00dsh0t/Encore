using Encore.Contracts.Messages;
using Encore.Core.Library;

namespace Encore.Core.Tests;

public sealed class LibraryIndexTests
{
    private readonly List<string> _published = [];
    private int _unsaved;

    [Fact]
    public void AWriteIsListedLockedThenChangedAndAFailedOneLeavesNothingToSave()
    {
        var index = NewIndex();
        var path = Path.Combine("C:", "Clips", "a.mp4");

        index.BeginWrite(path, new LibraryItem("a.mp4", null, 0, DateTimeOffset.UnixEpoch, true));
        Assert.True(index.IsBeingWritten(path));
        Assert.True(index.TryEndWrite(path, out var written));
        index.Change(written with { Size = 10, BeingWritten = false });

        index.BeginWrite(path, new LibraryItem("b.mp4", null, 0, DateTimeOffset.UnixEpoch, true));
        Assert.True(index.TryEndWrite(path, out var failed));
        index.DropFailedWrite(failed.Path);

        Assert.Equal(["added a.mp4", "changed a.mp4", "added b.mp4", "removed b.mp4"], _published);
        Assert.Equal(1, _unsaved);
        Assert.False(index.TryEndWrite(path, out _));
    }

    [Fact]
    public void ALengthIsAppliedOnlyAtTheSizeItWasReadAtAndFollowsAMove()
    {
        var index = NewIndex(new LibraryItem("a.mp4", null, 10, DateTimeOffset.UnixEpoch, false));
        var readAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Assert.True(index.NeedsLength("a.mp4", out var item));
        index.LengthQueued("a.mp4", item.Size);
        Assert.False(index.NeedsLength("a.mp4", out _));

        index.ApplyLength("a.mp4", size: 11, readAt, length: 5);
        Assert.Null(index.Items.Single().LengthSeconds);
        index.ApplyLength("a.mp4", size: 10, readAt, length: 5);
        Assert.Equal(5, index.Items.Single().LengthSeconds);

        var moved = index.Move(index.Items.Single(), "b.mp4");
        Assert.Equal(5, moved.LengthSeconds);
        Assert.Equal(readAt, Assert.Single(index.Entries()).Modified);
        Assert.Equal(["changed a.mp4", "removed a.mp4", "added b.mp4"], _published);
    }

    private LibraryIndex NewIndex(params LibraryItem[] items) => new(
        items,
        item => _published.Add("added " + item.Path),
        item => _published.Add("changed " + item.Path),
        removed => _published.Add("removed " + removed.Path),
        () => _unsaved++);
}
