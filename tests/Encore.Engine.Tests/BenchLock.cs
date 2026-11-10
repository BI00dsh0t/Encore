namespace Encore.Engine.Tests;

/// <summary>
/// <c>.bench-running</c> in the main folder: on-screen tests wait while another session holds it,
/// then hold it themselves.
/// </summary>
internal sealed class BenchLock(string path) : IDisposable
{
    public static BenchLock Take(string holder = "Engine on-screen capture test")
    {
        var path = Path.Combine(MainFolder(), ".bench-running");
        while (true)
        {
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                file.Write(System.Text.Encoding.UTF8.GetBytes(holder));
                return new BenchLock(path);
            }
            catch (IOException) when (File.Exists(path))
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }
    }

    public void Dispose() => File.Delete(path);

    // The topmost folder with Encore.slnx: the main checkout, not a worktree under it.
    private static string MainFolder()
    {
        string? found = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Encore.slnx")))
            {
                found = dir.FullName;
            }
        }

        return found ?? throw new InvalidOperationException("Encore.slnx wasn't found above the test.");
    }
}
