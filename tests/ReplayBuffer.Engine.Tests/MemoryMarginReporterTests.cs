using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Sessions;

namespace ReplayBuffer.Engine.Tests;

/// <summary>Each entry into the RAM margin, and each short ring inside it, is one <c>memory-margin</c> warning.</summary>
public sealed class MemoryMarginReporterTests
{
    [Fact]
    public void TwoEntriesAndAShortRingRaiseThreeMemoryMarginWarnings()
    {
        var hub = new EventHub(SettingsDocument.Empty);
        var status = new FakeMemoryStatus { Total = 32L << 30, Available = 1L << 30 };
        var margin = new MemoryMargin(status);
        using var reporter = new MemoryMarginReporter(margin, new Warnings(hub, TimeProvider.System));

        Assert.False(margin.Allows(4 << 20));
        margin.ReportShortRing();
        status.Available = 20L << 30;
        Assert.True(margin.Allows(4 << 20));
        status.Available = 1L << 30;
        Assert.False(margin.Allows(4 << 20));

        var warnings = hub.State.Warnings;
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w =>
        {
            Assert.Equal(ErrorCodes.MemoryMargin, w.Code);
            Assert.Equal(WarningSeverity.Warning, w.Severity);
            Assert.Equal("replay", w.Source);
            Assert.False(w.Overlay);
            Assert.True(w.Dismissable);
        });
    }

    private sealed class FakeMemoryStatus : IMemoryStatus
    {
        public long Available { get; set; }

        public long Total { get; set; }

        public MemoryStatus Read() => new(Available, Total);
    }
}
