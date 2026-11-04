using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Platform.Windows.Tests;

public sealed class PowerNotificationsTests
{
    [Theory]
    [InlineData(0x4u, PowerChange.Suspending)]
    [InlineData(0x12u, PowerChange.Resumed)]
    [InlineData(0x7u, PowerChange.Resumed)]
    [InlineData(0x8013u, null)]
    public void MapTakesSuspendAndBothResumesAndIgnoresTheRest(uint type, PowerChange? expected) =>
        Assert.Equal(expected, SuspendResumeNotifications.Map(type));
}
