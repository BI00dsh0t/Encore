namespace Encore.Engine.Tests;

public sealed class CaptureStatsFileTests
{
    [Fact]
    public void CheckingAPathMakesItsFolderAndLeavesNoFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "encore-stats-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "stats.json");
        try
        {
            CaptureStatsFile.EnsureWritable(path);

            Assert.True(Directory.Exists(folder));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
