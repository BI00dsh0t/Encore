using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Engine.Settings;

/// <summary>
/// The engine's runtime state (AD-5): <c>runtime.json</c> in the instance's local data folder,
/// <c>{ "micMuted": bool }</c>. It is never a setting and never in a snapshot. A missing or
/// unreadable file means muted; writes are atomic (a flushed temp file moved over the old one), and
/// a failed write is logged and leaves the old file.
/// </summary>
internal sealed class RuntimeStateStore : IMicMuteStore
{
    public const string FileName = "runtime.json";

    private readonly ILogger _log;
    private readonly string _folder;
    private readonly string _path;
    private readonly string _tempPath;

    /// <param name="folder">Normally <see cref="Contracts.Instance.LocalDataFolder"/>.</param>
    public RuntimeStateStore(string folder, ILogger log)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(log);
        _folder = Path.GetFullPath(folder);
        _path = Path.Combine(_folder, FileName);
        _tempPath = _path + ".tmp";
        _log = log;
    }

    public string FilePath => _path;

    public bool Load()
    {
        byte[] json;
        try
        {
            json = File.ReadAllBytes(_path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _log.LogInformation("No {File} yet: the mic starts muted", FileName);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("{Path} could not be read: {Message}; the mic starts muted", _path, e.Message);
            return true;
        }

        RuntimeState? state;
        try
        {
            state = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.RuntimeState);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (state?.MicMuted is not { } muted)
        {
            _log.LogWarning("{Path} is not a readable runtime state file; the mic starts muted", _path);
            return true;
        }

        _log.LogInformation("Mic mute loaded from {File}: {State}", FileName, muted ? "muted" : "unmuted");
        return muted;
    }

    public void Save(bool muted)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new RuntimeState(muted), RuntimeJsonContext.Default.RuntimeState);
                stream.Flush(flushToDisk: true);
            }

            File.Move(_tempPath, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(_tempPath);
            _log.LogWarning("Mic mute ({State}) could not be saved to {Path}: {Message}; it holds until the engine stops",
                muted ? "muted" : "unmuted", _path, e.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp file that can't be deleted is overwritten by the next save.
        }
    }
}

/// <summary><c>runtime.json</c>'s shape; a missing <c>micMuted</c> makes the file unreadable.</summary>
internal sealed record RuntimeState(bool? MicMuted);

[JsonSourceGenerationOptions(JsonSerializerDefaults.General, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RuntimeState))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext;
