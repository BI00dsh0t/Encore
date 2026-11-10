namespace Encore.Bench.Tests;

/// <summary>
/// The tests that run Bench's subcommands against real processes, devices and Hotkeys: one at a time, so a
/// held combination, the audio devices or a stray engine never meet another test's.
/// </summary>
[CollectionDefinition(nameof(BenchProcessCollection), DisableParallelization = true)]
public sealed class BenchProcessCollection;

/// <summary>A throwaway run folder, and Bench's options and context pointed at it.</summary>
internal sealed class BenchRun : IDisposable
{
    public BenchRun() => Folder = Directory.CreateTempSubdirectory("bench-cmd-").FullName;

    public string Folder { get; }

    /// <summary>
    /// The engine built beside these tests (same configuration and target framework), or the folder
    /// <c>REPLAYBUFFER_ENGINE_DIR</c> names; null when there is none.
    /// </summary>
    public static string? EngineExe
    {
        get
        {
            if (Environment.GetEnvironmentVariable("REPLAYBUFFER_ENGINE_DIR") is { Length: > 0 } dir)
            {
                var overridden = Path.Combine(Path.GetFullPath(dir), "Encore.Engine.exe");
                return File.Exists(overridden) ? overridden : null;
            }

            var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = tfm.Parent?.Name;
            if (configuration is null)
            {
                return null;
            }

            var exe = Path.Combine(BenchContext.FindRepoRoot(null), "src", "Encore.Engine", "bin", configuration, tfm.Name, "Encore.Engine.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    public BenchContext Context(params string[] args) => BenchContext.Create(BenchOptions.Parse([.. args, "--out", Folder]));

    public string PathOf(string file) => Path.Combine(Folder, file);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A process that just exited may still hold a log file; the temp folder is left behind.
        }
    }
}
