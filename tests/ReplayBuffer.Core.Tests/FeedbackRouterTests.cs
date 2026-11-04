using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Feedback;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

public sealed class FeedbackRouterTests
{
    [Fact]
    public void ASavedClipRaisesOneNoticeThatIsWithdrawnTenSecondsLater()
    {
        var notices = new FakeFeedbackNotices();
        var time = new ManualTime();
        using var router = new FeedbackRouter(notices, time);

        router.ClipSaved(new ClipSaved(@"c:\clips\a.mp4"));

        var notice = Assert.Single(notices.Active).Value;
        Assert.Equal(new FeedbackNotice(ErrorCodes.ClipSaved, WarningSeverity.Info, "clips", "Clip saved", "clipSaved"), notice);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Single(notices.Active);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(notices.Active);
    }

    [Fact]
    public void AFailedSaveStaysUntilTheNextSavedClip()
    {
        var notices = new FakeFeedbackNotices();
        var time = new ManualTime();
        using var router = new FeedbackRouter(notices, time);

        router.ClipNotSaved(new ClipFailed(ErrorCodes.DiskFull));

        var failed = Assert.Single(notices.Active).Value;
        Assert.Equal(ErrorCodes.ClipFailed, failed.Code);
        Assert.Equal(WarningSeverity.Error, failed.Severity);
        Assert.Equal("Clip not saved: disk full", failed.Message);
        Assert.Equal("saveFailed", failed.Sound);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(notices.Active);

        router.ClipSaved(new ClipSaved(@"c:\clips\b.mp4"));

        Assert.Equal(ErrorCodes.ClipSaved, Assert.Single(notices.Active).Value.Code);
    }

    [Fact]
    public void AnEncoderRestartRaisesOneWarningNamingBothFiles()
    {
        var notices = new FakeFeedbackNotices();
        var time = new ManualTime();
        using var router = new FeedbackRouter(notices, time);

        router.RecordingRestarted(new RecordingRestart(@"c:\clips\Game\a.mp4", @"c:\clips\Game\b.mp4"));

        var notice = Assert.Single(notices.Active).Value;
        Assert.Equal(ErrorCodes.RecordingRestarted, notice.Code);
        Assert.Equal(WarningSeverity.Warning, notice.Severity);
        Assert.Equal("recordingStarted", notice.Sound);
        Assert.Contains("a.mp4", notice.Message, StringComparison.Ordinal);
        Assert.Contains("b.mp4", notice.Message, StringComparison.Ordinal);
        time.Advance(FeedbackRouter.InfoLifetime);
        Assert.Empty(notices.Active);
    }

    [Fact]
    public void ADiskFullStaysUntilTheNextRecordingAndAStoppedReplayBufferUntilItResumes()
    {
        var notices = new FakeFeedbackNotices();
        var time = new ManualTime();
        using var router = new FeedbackRouter(notices, time);

        router.DiskFull();
        router.ReplayStoppedDiskFull();

        Assert.Equal(2, notices.Active.Count);
        var stopped = Assert.Single(notices.Active.Values, n => n.Code == ErrorCodes.ReplayStoppedDiskFull);
        Assert.Equal(WarningSeverity.Warning, stopped.Severity);
        Assert.Null(stopped.Sound);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, notices.Active.Count);

        router.ReplayResumed();
        Assert.Equal(ErrorCodes.DiskFull, Assert.Single(notices.Active).Value.Code);

        router.RecordingStarted(new RecordingStarted(@"c:\clips\Game\a.mp4"));
        Assert.Equal(ErrorCodes.RecordingStarted, Assert.Single(notices.Active).Value.Code);
    }

    [Fact]
    public void ADiskFullOnARecordingAnEncoderRestartReplacedKeepsItsNoticeAndRaisesNoStart()
    {
        var notices = new FakeFeedbackNotices();
        using var router = new FeedbackRouter(notices, new ManualTime());

        router.StateChanged(CaptureState.Initial with { Recording = RecordingState.Running });
        router.RecordingStopped(new RecordingStopped(@"c:\clips\Game.mp4", RecordingStopReasons.DiskFull));
        router.DiskFull();
        router.RecordingStarted(new RecordingStarted(@"c:\clips\Game.mp4"));
        router.RecordingRestarted(new RecordingRestart(@"c:\clips\Game.mp4", @"c:\clips\Game.mp4"));

        Assert.Equal(
            [ErrorCodes.RecordingStopped, ErrorCodes.DiskFull, ErrorCodes.RecordingRestarted],
            notices.Active.Values.Select(n => n.Code));
    }

    [Fact]
    public void AMonitorRecordingRaisesItsOwnNoticesBesideAGameRecordingAndLeavesItsHandoverAlone()
    {
        var notices = new FakeFeedbackNotices();
        using var router = new FeedbackRouter(notices, new ManualTime());

        router.StateChanged(CaptureState.Initial with { Recording = RecordingState.Running, MonitorRecording = RecordingState.Running });
        router.MonitorRecordingStarted(new RecordingStarted(@"c:\clips\Monitor Recordings\Monitor.mp4"));
        router.MonitorRecordingStopped(new RecordingStopped(@"c:\clips\Monitor Recordings\Monitor.mp4", RecordingStopReasons.Stopped));

        // The game Recording's next start is still a new one, not an encoder restart's replacement.
        router.RecordingStarted(new RecordingStarted(@"c:\clips\Game\Game.mp4"));

        Assert.Equal(
            [
                new FeedbackNotice(ErrorCodes.RecordingStarted, WarningSeverity.Info, "recordings", "Monitor Recording started", "recordingStarted"),
                new FeedbackNotice(ErrorCodes.RecordingStopped, WarningSeverity.Info, "recordings", "Monitor Recording stopped", "recordingStopped"),
                new FeedbackNotice(ErrorCodes.RecordingStarted, WarningSeverity.Info, "recordings", "Recording started", "recordingStarted"),
            ],
            notices.Active.Values);
    }
}
