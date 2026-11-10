namespace Encore.Platform.FFmpeg.Tests;

public sealed class KeyframeRequestsTests
{
    [Fact]
    public void AFrameCapturedAfterThePressServesTheRequest()
    {
        var keyframes = new KeyframeRequests();
        var ticket = keyframes.Request(afterQpc: 100);

        Assert.False(keyframes.IsDue(frameQpc: 100, out _));
        Assert.True(keyframes.IsDue(frameQpc: 101, out var requested));
        Assert.False(keyframes.TryGetServed(ticket, out _));

        keyframes.MarkServed(pts: 9_000, requested);

        Assert.True(keyframes.TryGetServed(ticket, out var pts));
        Assert.Equal(9_000, pts);
        Assert.False(keyframes.IsDue(frameQpc: 200, out _));
    }
}
