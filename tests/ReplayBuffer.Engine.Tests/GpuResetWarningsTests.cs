using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Sessions;

namespace ReplayBuffer.Engine.Tests;

/// <summary>Story 8.3: the status line shows <c>gpu-reset</c> only while a session recovers.</summary>
public sealed class GpuResetWarningsTests
{
    [Fact]
    public void ARecoveryWithdrawsTheGpuResetWarningAndAGiveUpReplacesItWithGpuResetFailed()
    {
        var hub = new EventHub(SettingsDocument.Empty);
        var recovery = new GpuResetWarnings(new Warnings(hub, TimeProvider.System), NullLogger.Instance).Recovery;

        recovery.Reset!("The GPU driver reset");
        var shown = Assert.Single(hub.State.Warnings);
        Assert.Equal((ErrorCodes.GpuReset, WarningSeverity.Warning, false), (shown.Code, shown.Severity, shown.Overlay));

        recovery.Recovered!(TimeSpan.FromSeconds(1));
        Assert.Empty(hub.State.Warnings);

        recovery.Reset!("The GPU driver reset again");
        recovery.Failed!("It couldn't recover");
        var failed = Assert.Single(hub.State.Warnings);
        Assert.Equal((ErrorCodes.GpuResetFailed, WarningSeverity.Error, false), (failed.Code, failed.Severity, failed.Overlay));
    }
}
