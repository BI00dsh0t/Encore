namespace Encore.Platform.Audio.Tests;

public sealed class EndpointMatchTests
{
    private static readonly AudioEndpoint Game = new("{0.0.0}.{a}", "Game (BEACN Mix Create)", AudioFlow.Output);
    private static readonly AudioEndpoint Chat = new("{0.0.0}.{b}", "Chat (BEACN Mix Create)", AudioFlow.Output);
    private static readonly AudioEndpoint Headphones = new("{0.0.0}.{c}", "Headphones (BEACN Mic)", AudioFlow.Output);
    private static readonly AudioEndpoint Mic = new("{0.0.1}.{d}", "Microphone (BEACN Mic)", AudioFlow.Input);
    private static readonly AudioEndpoint Mic2 = new("{0.0.1}.{e}", "Microphone (BEACN Mic) 2", AudioFlow.Input);

    private static readonly IReadOnlyList<AudioEndpoint> Outputs = [Game, Chat, Headphones];

    [Fact]
    public void ASingleSubstringMatchIsFound()
    {
        var match = AudioEndpoints.Match(Outputs, "game");

        Assert.Equal(EndpointMatchOutcome.Found, match.Outcome);
        Assert.Same(Game, match.Endpoint);
    }

    [Fact]
    public void NoMatchListsEveryEndpoint()
    {
        var match = AudioEndpoints.Match(Outputs, "Schiit");

        Assert.Equal(EndpointMatchOutcome.NotFound, match.Outcome);
        Assert.Null(match.Endpoint);
        Assert.Equal(Outputs, match.Candidates);
    }

    [Fact]
    public void SeveralMatchesAreAmbiguousAndListOnlyTheMatches()
    {
        var match = AudioEndpoints.Match(Outputs, "BEACN Mix");

        Assert.Equal(EndpointMatchOutcome.Ambiguous, match.Outcome);
        Assert.Null(match.Endpoint);
        Assert.Equal([Game, Chat], match.Candidates);
    }

    [Fact]
    public void AnExactNameWinsOverLongerNamesThatContainIt()
    {
        var match = AudioEndpoints.Match([Mic, Mic2], "microphone (beacn mic)");

        Assert.Equal(EndpointMatchOutcome.Found, match.Outcome);
        Assert.Same(Mic, match.Endpoint);
    }

    [Fact]
    public void AnEndpointIdMatchesExactly()
    {
        var match = AudioEndpoints.Match(Outputs, "{0.0.0}.{b}");

        Assert.Equal(EndpointMatchOutcome.Found, match.Outcome);
        Assert.Same(Chat, match.Endpoint);
    }
}
