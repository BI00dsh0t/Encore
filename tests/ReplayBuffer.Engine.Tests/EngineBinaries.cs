namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The folder holding the engine executable (and ffprobe) that the process tests run: the build
/// output beside this assembly, or <c>REPLAYBUFFER_ENGINE_DIR</c> when set, so the same tests run
/// against the NativeAOT publish of <c>tools/publish-engine.ps1</c>.
/// </summary>
internal static class EngineBinaries
{
    public const string FolderVariable = "REPLAYBUFFER_ENGINE_DIR";

    public static string Folder { get; } = Resolve();

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(FolderVariable);
        if (string.IsNullOrEmpty(overridden))
        {
            return Path.GetDirectoryName(typeof(CommandLine).Assembly.Location)!;
        }

        // An override must hold the FFmpeg payload too, or the tests that need it would skip, not fail.
        var folder = Path.GetFullPath(overridden);
        foreach (var file in new[] { "ReplayBuffer.Engine.exe", "avcodec-63.dll", "ffprobe.exe" })
        {
            if (!File.Exists(Path.Combine(folder, file)))
            {
                throw new InvalidOperationException($"{FolderVariable} is '{folder}', which has no {file}.");
            }
        }

        return folder;
    }
}
