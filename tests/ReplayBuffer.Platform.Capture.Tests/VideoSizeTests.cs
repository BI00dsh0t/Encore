namespace ReplayBuffer.Platform.Capture.Tests;

public sealed class VideoSizeTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1919, 2088, 1918, 2088)]
    public void TheEncoderSizeIsTheContentSizeRoundedDownToEven(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new VideoSize(expectedWidth, expectedHeight), VideoSize.EvenFloor(width, height));
    }

    [Fact]
    public void AResizedFrameCopiesOnlyTheOverlap()
    {
        var output = new VideoSize(1920, 1080);

        Assert.Equal(new VideoSize(1280, 1080), new VideoSize(1280, 1440).Intersect(output));
        Assert.Equal(output, new VideoSize(2560, 1440).Intersect(output));
    }
}
