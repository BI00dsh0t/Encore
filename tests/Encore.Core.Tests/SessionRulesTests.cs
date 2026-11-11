using System.Diagnostics;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;

namespace Encore.Core.Tests;

/// <summary>The orchestrator's state rules, each on its own.</summary>
public sealed class SessionRulesTests
{
    private static readonly TargetCandidate Alpha = new(10, 1, "alpha.exe", "Alpha");
    private static readonly TargetCandidate Beta = new(20, 2, "beta.exe", "Beta");

    [Fact]
    public void ATargetSwitchIsHeldOnlyWhileARecordingRunsAndTheOldGameStays()
    {
        Assert.Equal(TargetDecision.SameProcess, TargetSwitch.Decide(Alpha, Alpha with { Window = 9 }, TargetChangeReason.Focus, recording: true));
        Assert.Equal(TargetDecision.Held, TargetSwitch.Decide(Alpha, Beta, TargetChangeReason.Focus, recording: true));
        Assert.Equal(TargetDecision.Switch, TargetSwitch.Decide(Alpha, Beta, TargetChangeReason.Focus, recording: false));
        Assert.Equal(TargetDecision.Switch, TargetSwitch.Decide(Alpha, Beta, TargetChangeReason.Exit, recording: true));
        Assert.Equal(TargetDecision.Nothing, TargetSwitch.Decide(null, null, TargetChangeReason.Exit, recording: false));
        Assert.Equal(RecordingStopReasons.GameExit, TargetSwitch.RecordingReasonFor(TargetChangeReason.Exit));
    }

    [Fact]
    public void AChangedChainWaitsForARecordingThenForAResetBesideAMonitorRecording()
    {
        Assert.Equal(ChainDecision.Keep, ChainChange.Decide(sameChain: true, recordingOnSession: true, monitorRuns: true));
        Assert.Equal(ChainDecision.HeldByRecording, ChainChange.Decide(sameChain: false, recordingOnSession: true, monitorRuns: true));
        Assert.Equal(ChainDecision.SwitchAtReset, ChainChange.Decide(sameChain: false, recordingOnSession: false, monitorRuns: true));
        Assert.Equal(ChainDecision.PrepareAhead, ChainChange.Decide(sameChain: false, recordingOnSession: false, monitorRuns: false));
    }

    [Fact]
    public void ASavePressInsideTheCooldownIsRefusedAndCountsFromTheLastAcceptedPress()
    {
        var cooldown = new SaveCooldown();
        var second = Stopwatch.Frequency;

        Assert.Null(cooldown.Accept(100 * second, 5 * second));
        Assert.Equal(TimeSpan.FromSeconds(2), cooldown.Accept(102 * second, 5 * second));
        Assert.Null(cooldown.Accept(105 * second, 5 * second));
    }

    [Fact]
    public void AMonitorChangeNamesTheScreensThatArrivedAndTheOnesThatWent()
    {
        var change = MonitorChange.Compare(
            [@"\\.\DISPLAY1", @"\\.\DISPLAY2"],
            [new DisplayMonitor(@"\\.\display1", "One", 1920, 1080, true), new DisplayMonitor(@"\\.\DISPLAY3", "Three", 1920, 1080, false)]);

        Assert.Equal([@"\\.\DISPLAY3"], change.Arrived);
        Assert.Equal([@"\\.\DISPLAY2"], change.Gone);
    }
}
