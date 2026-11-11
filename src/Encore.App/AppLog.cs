using System.Globalization;
using System.IO;

namespace Encore.App;

/// <summary>The App's log file, <c>logs\app.log</c> in the instance's local data folder. Any thread.</summary>
internal sealed class AppLog(string localDataFolder)
{
    private readonly Lock _lock = new();

    /// <summary>Writes one timestamped line; a line that can't be written is dropped.</summary>
    public void Write(string message)
    {
        var time = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var line = $"{time} {message}{Environment.NewLine}";
        try
        {
            var folder = Path.Combine(localDataFolder, "logs");
            Directory.CreateDirectory(folder);
            lock (_lock)
            {
                File.AppendAllText(Path.Combine(folder, "app.log"), line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is best effort; the status line carries the same text.
        }
    }
}
