using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

public sealed class ClipClaimTests
{
    [Fact]
    public void AConcurrentWriteStartsAtTheEarlierEndAndFailuresInEitherOrderLeaveTheLastSucceededEnd()
    {
        // Write A claims its end; write B, started while A writes, cuts from there.
        var claim = new ClipClaim();
        Assert.Equal(long.MinValue, claim.Claim(10));
        Assert.Equal(10, claim.Claim(20));

        // A fails while B writes: B's claim stands. Then B fails: nothing is claimed.
        claim.Failed(10);
        Assert.Equal(20, claim.Start);
        claim.Failed(20);
        Assert.Equal(long.MinValue, claim.Start);

        // After a success at 30, two concurrent failures, B first then A, fall back to 30.
        claim.Claim(30);
        claim.Succeeded(30, 30);
        Assert.Equal(30, claim.Claim(40));
        Assert.Equal(40, claim.Claim(50));
        claim.Failed(50);
        claim.Failed(40);
        Assert.Equal(30, claim.Start);
    }
}
