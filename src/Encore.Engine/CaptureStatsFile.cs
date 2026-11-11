using System.Text.Json;

namespace Encore.Engine;

/// <summary>The stats file on disk: checked before a run, written once after it.</summary>
internal static class CaptureStatsFile
{
    /// <summary>Checks before a run that the path can be written; a file the check created is removed again.</summary>
    public static void EnsureWritable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
        }

        if (!existed)
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes the report as JSON to a temp file, then moves it into place, so a failed write leaves no cut-off file.</summary>
    public static void Write(string path, CaptureStatsReport report)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, report, CaptureStatsJson.Default.CaptureStatsReport);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
