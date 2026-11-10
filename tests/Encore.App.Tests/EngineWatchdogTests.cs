using Encore.App.Engine;
using Encore.Contracts;

namespace Encore.App.Tests;

public sealed class EngineWatchdogTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private readonly ManualTime _time = new();

    [Fact]
    public void AFreshConnectionIsAlive()
    {
        Assert.Null(new EngineWatchdog(_time).Check());
    }

    [Fact]
    public void SilenceJustUnderSixSecondsIsAliveAndSixIsHung()
    {
        var watchdog = new EngineWatchdog(_time);

        _time.Advance(Supervision.SilenceLimit - Tick);
        Assert.Null(watchdog.Check());

        _time.Advance(Tick);
        Assert.Contains("sent nothing", watchdog.Check());
    }

    [Fact]
    public void AnyEventResetsTheSilence()
    {
        var watchdog = new EngineWatchdog(_time);
        _time.Advance(TimeSpan.FromSeconds(5));

        watchdog.EventReceived();
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(watchdog.Check());
    }

    [Fact]
    public void ACommandWithNoReplyForTenSecondsIsHungEvenWithEventsFlowing()
    {
        var watchdog = new EngineWatchdog(_time);
        watchdog.CommandStarted();
        for (var i = 0; i < 9; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            watchdog.EventReceived();
        }

        _time.Advance(TimeSpan.FromSeconds(1) - Tick);
        watchdog.EventReceived();
        Assert.Null(watchdog.Check());

        _time.Advance(Tick);
        Assert.Contains("no reply", watchdog.Check());
    }

    [Fact]
    public void AnsweredCommandsStopCountingAndOnlyTheOldestMatters()
    {
        var watchdog = new EngineWatchdog(_time);
        var first = watchdog.CommandStarted();
        _time.Advance(TimeSpan.FromSeconds(5));
        watchdog.CommandStarted();
        watchdog.CommandEnded(first);
        _time.Advance(TimeSpan.FromSeconds(5));
        watchdog.EventReceived();

        Assert.Null(watchdog.Check());

        _time.Advance(TimeSpan.FromSeconds(5));
        watchdog.EventReceived();
        Assert.NotNull(watchdog.Check());
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
