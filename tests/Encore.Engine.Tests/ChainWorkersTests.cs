using Microsoft.Extensions.Logging.Abstractions;
using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

public sealed class ChainWorkersTests
{
    [Fact]
    public void AStartedWorkerRunsItsWorkAndIsHandedOverOnce()
    {
        var workers = new ChainWorkers(NullLogger.Instance);
        using var ran = new ManualResetEventSlim();

        workers.Start("Test worker", ran.Set);
        var taken = workers.TakeAll();

        var worker = Assert.Single(taken);
        Assert.Equal("Test worker", worker.Name);
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.True(ran.IsSet);
        Assert.Empty(workers.TakeAll());
    }
}
