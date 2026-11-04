using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Engine.Sessions;

namespace ReplayBuffer.Engine.Tests;

public sealed class ReplayRingsTests
{
    [Fact]
    public void ARingFolderThatCantBeMadeGivesAMemoryRing()
    {
        // A file where the folder should be: the ring file can't be created under it.
        var blocker = Path.GetTempFileName();
        try
        {
            using var ring = ReplayRings.Create(30_000_000, 64, blocker, 16L * 1024 * 1024, margin: null, NullLogger.Instance);

            Assert.Null(ring.File);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
