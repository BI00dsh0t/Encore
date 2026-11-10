using Encore.Contracts.Settings;
using Encore.Core.Library;

namespace Encore.Core.Tests;

/// <summary>The Library actor's state rules, each on its own.</summary>
public sealed class LibraryRulesTests
{
    [Fact]
    public void AnOwnOperationIsCoveredUntilItsLatestRegistrationExpires()
    {
        var time = new ManualTime();
        var expired = new List<(string Path, long Id)>();
        var operations = new OwnFileOperations(time, (path, id) => expired.Add((path, id)));

        operations.Register("a.mp4");
        time.Advance(TimeSpan.FromSeconds(3));
        operations.Register("a.mp4");
        time.Advance(TimeSpan.FromSeconds(3));

        // The first registration's timer was stopped when the path was registered again.
        Assert.Empty(expired);
        Assert.True(operations.Covers("A.MP4"));
        Assert.False(operations.Expire("a.mp4", 1));

        time.Advance(TimeSpan.FromSeconds(2));
        var (path, id) = Assert.Single(expired);
        Assert.True(operations.Expire(path, id));
        Assert.False(operations.Covers("a.mp4"));
    }

    [Fact]
    public void PendingPathsSettleOnceAndARescanTakesTheirPlace()
    {
        var time = new ManualTime();
        var settled = 0;
        var pending = new PendingChecks();
        pending.Start(time, () => settled++);

        pending.Add("a.mp4");
        time.Advance(TimeSpan.FromMilliseconds(200));
        pending.Add("b.mp4");
        time.Advance(TimeSpan.FromMilliseconds(50));

        // The second path didn't push the timer back.
        Assert.Equal(1, settled);
        pending.Settled();
        Assert.Equal(["a.mp4", "b.mp4"], pending.TakePaths().Order().ToList());
        Assert.False(pending.Contains("a.mp4"));

        pending.Add("c.mp4");
        pending.AddRescan();
        Assert.True(pending.RescanPending);
        pending.Clear();
        Assert.False(pending.RescanPending);
        Assert.Empty(pending.TakePaths());
    }

    [Fact]
    public void TheSizeWarningIsRaisedOnceUntilTheFolderIsBackWithinItsLimit()
    {
        var warning = new CleanupLimitWarning();
        var over = new CleanupPlan([], Bytes: 0, Remaining: 20, Unreachable: true);
        var within = new CleanupPlan([], Bytes: 0, Remaining: 5, Unreachable: false);

        Assert.True(warning.IsDue(10, over));
        Assert.False(warning.IsDue(10, over));
        Assert.False(warning.IsDue(10, within));
        Assert.True(warning.IsDue(10, over));

        // Turning Cleanup off re-arms it too; a limit that is still set doesn't.
        warning.RearmWhenOff(new CleanupSettings(10, null));
        Assert.False(warning.IsDue(10, over));
        warning.RearmWhenOff(new CleanupSettings(null, null));
        Assert.True(warning.IsDue(10, over));
    }
}
