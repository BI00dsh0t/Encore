namespace Encore.Platform.Windows.Tests;

public sealed class WindowMatchTests
{
    private static readonly WindowInfo League = new(0x10, "League of Legends", 1, "LeagueClientUx.exe");
    private static readonly WindowInfo Client = new(0x20, "Riot Client", 2, "Riot Client.exe");
    private static readonly WindowInfo Steam = new(0x30, "Steam", 3, "steamwebhelper.exe");
    private static readonly WindowInfo SteamSettings = new(0x40, "Steam Settings", 3, "steamwebhelper.exe");

    private static readonly IReadOnlyList<WindowInfo> Windows = [League, Client, Steam, SteamSettings];

    [Fact]
    public void ASingleSubstringMatchIsFoundIgnoringCase()
    {
        var match = WindowFinder.Match(Windows, "league");

        Assert.Equal(WindowMatchOutcome.Found, match.Outcome);
        Assert.Same(League, match.Window);
    }

    [Fact]
    public void NoMatchListsEveryWindow()
    {
        var match = WindowFinder.Match(Windows, "Valorant");

        Assert.Equal(WindowMatchOutcome.NotFound, match.Outcome);
        Assert.Null(match.Window);
        Assert.Equal(Windows, match.Candidates);
    }

    [Fact]
    public void SeveralMatchesAreAmbiguousAndListOnlyTheMatches()
    {
        var match = WindowFinder.Match(Windows, "i");

        Assert.Equal(WindowMatchOutcome.Ambiguous, match.Outcome);
        Assert.Null(match.Window);
        Assert.Equal([Client, SteamSettings], match.Candidates);
    }

    [Fact]
    public void AnExactTitleWinsOverLongerTitlesThatContainIt()
    {
        var match = WindowFinder.Match(Windows, "STEAM");

        Assert.Equal(WindowMatchOutcome.Found, match.Outcome);
        Assert.Same(Steam, match.Window);
    }

    [Fact]
    public void ListingReturnsOnlyVisibleTitledWindows()
    {
        var windows = WindowFinder.List();

        Assert.All(windows, w =>
        {
            Assert.NotEqual(0, w.Handle);
            Assert.False(string.IsNullOrWhiteSpace(w.Title));
        });
    }

    [Fact]
    public void AHandleThatIsNotAWindowResolvesToNothing()
    {
        Assert.Null(WindowFinder.FromHandle(0x7FFFFFF0));
    }
}
