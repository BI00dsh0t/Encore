using Microsoft.Extensions.Logging;
using ReplayBuffer.Engine.Logging;

namespace ReplayBuffer.Engine.Tests;

public sealed class TextLoggerProviderTests
{
    [Fact]
    public void LinesReachTheDailyFileByTheTimeTheProviderIsDisposed()
    {
        var folder = Directory.CreateTempSubdirectory("rb-logs-").FullName;
        try
        {
            using (var provider = new TextLoggerProvider(folder, "engine", consoleLevel: LogLevel.None))
            {
                var log = provider.CreateLogger("Test");
                log.LogDebug("debug {Value}", 1);
                log.LogError(new InvalidOperationException("boom"), "failed {Code}", "encode-failed");
            }

            var file = Assert.Single(Directory.GetFiles(folder, "engine-*.log"));
            Assert.Matches(@"engine-\d{8}\.log$", file);
            var text = File.ReadAllText(file);
            Assert.Contains("[DBG] Test: debug 1", text);
            Assert.Contains("[ERR] Test: failed encode-failed", text);
            Assert.Contains("System.InvalidOperationException: boom", text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
