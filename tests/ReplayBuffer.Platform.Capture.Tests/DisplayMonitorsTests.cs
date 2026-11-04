using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayBuffer.Platform.Capture.Tests;

/// <summary>The engine's display port (8.5, T-03), on the real displays, which it only reads.</summary>
public sealed class DisplayMonitorsTests
{
    [Fact]
    public async Task ItListsThePrimaryFirstAndReportsABurstOfDisplayChangesOnce()
    {
        var displays = new DisplayMonitors(NullLogger.Instance);
        var list = displays.List();
        Assert.NotEmpty(list);
        Assert.True(list[0].Primary);

        var changes = 0;
        var subscription = displays.Subscribe(() => Interlocked.Increment(ref changes));
        try
        {
            // Two in a row, as a hot-plug sends: one report, a settle after the last.
            Assert.True(displays.SimulateDisplayChange());
            Assert.True(displays.SimulateDisplayChange());
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref changes) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            await Task.Delay(DisplayMonitors.Settle * 2, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref changes));
        }
        finally
        {
            subscription.Dispose();
        }

        // The last subscription stops the window.
        Assert.False(displays.SimulateDisplayChange());
    }
}
