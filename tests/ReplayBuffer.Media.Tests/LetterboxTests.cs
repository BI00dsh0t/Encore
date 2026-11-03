namespace ReplayBuffer.Media.Tests;

/// <summary>The fit rectangle of story 1.11's matrix: equal, narrower and wider content, and the resize phases.</summary>
public sealed class LetterboxTests
{
    [Theory]
    [InlineData(1440, 1080, 240, 0, 1440, 1080)] // narrower: 240 px bars left and right
    [InlineData(2560, 1080, 0, 134, 1920, 810)]  // wider: 1920×810, bars top and bottom
    public void ContentFitsCentredWithItsAspectKept(int contentWidth, int contentHeight, int x, int y, int width, int height)
    {
        var rect = Letterbox.Fit(contentWidth, contentHeight, 1920, 1080);
        Assert.Equal(new LetterboxRect(x, y, width, height), rect);
    }

    [Theory]
    [InlineData(1023, 767)]
    [InlineData(641, 481)]
    public void EveryEdgeIsEvenAndInsideTheOutput(int contentWidth, int contentHeight)
    {
        var rect = Letterbox.Fit(contentWidth, contentHeight, 1920, 1080);

        Assert.All(new[] { rect.X, rect.Y, rect.Width, rect.Height }, v => Assert.Equal(0, v % 2));
        Assert.InRange(rect.Width, 2, 1920);
        Assert.InRange(rect.Height, 2, 1080);
        Assert.InRange(rect.X + rect.Width, 2, 1920);
        Assert.InRange(rect.Y + rect.Height, 2, 1080);

        // Centred: the two bars differ by at most 2 px.
        Assert.InRange(1920 - rect.Width - (2 * rect.X), 0, 2);
        Assert.InRange(1080 - rect.Height - (2 * rect.Y), 0, 2);
    }

    [Fact]
    public void TheAspectRatioIsKeptToWithinAnEvenPixel()
    {
        var rect = Letterbox.Fit(1023, 767, 1920, 1080);
        var expectedWidth = 1023.0 * 1080 / 767;
        Assert.Equal(1080, rect.Height);
        Assert.InRange(rect.Width, expectedWidth - 1, expectedWidth + 1);
    }

    [Fact]
    public void EmptyContentFillsTheOutput() =>
        Assert.True(Letterbox.Fit(0, 0, 1280, 720).Covers(1280, 720));
}
