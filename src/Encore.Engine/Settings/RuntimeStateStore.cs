using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Encore.Core.Sessions;

namespace Encore.Engine.Settings;

/// <summary>
/// The engine's runtime state (AD-5): <c>runtime.json</c> in the instance's local data folder,
/// <c>{ "micMuted": bool }</c>, and, while a Recording is being written, its path in
/// <c>openRecordings</c> (FR-12), so the next start can tell which ones a dead engine left open. It
/// is never a setting and never in a snapshot. A missing or unreadable file means muted; writes are
/// atomic (a flushed temp file moved over the old one), and a failed write is logged and leaves the
/// old file. Both values are kept in memory and written together on every change.
/// </summary>
internal sealed class RuntimeStateStore : IMicMuteStore, IOpenRecordings
{
    public const string FileName = "runtime.json";

    private readonly ILogger _log;
    private readonly string _folder;
    private readonly string _path;
    private readonly string _tempPath;
    private readonly Lock _lock = new();
    private readonly List<string> _open = [];
    private bool? _muted;
    private bool _interrupted;

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
            return Loaded(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("{Path} could not be read: {Message}; the mic starts muted", _path, e.Message);
            return Loaded(true);
        }

        if (Parse(json)?.MicMuted is not { } muted)
        {
            _log.LogWarning("{Path} is not a readable runtime state file; the mic starts muted", _path);
            return Loaded(true);
        }

        _log.LogInformation("Mic mute loaded from {File}: {State}", FileName, muted ? "muted" : "unmuted");
        return Loaded(muted);
    }

    public void Save(bool muted)
    {
        lock (_lock)
        {
            _muted = muted;
            if (Write() is { } error)
            {
                _log.LogWarning("Mic mute ({State}) could not be saved to {Path}: {Message}; it holds until the engine stops", muted ? "muted" : "unmuted", _path, error);
            }
        }
    }

    public void Opened(string path)
    {
        lock (_lock)
        {
            _open.Add(path);
            if (Write() is { } error)
            {
                _log.LogWarning("The open Recording {Recording} could not be saved to {Path}: {Message}; if the engine dies, it won't be reported", path, _path, error);
            }
        }
    }

    public void Closed(string path)
    {
        lock (_lock)
        {
            if (_open.Remove(path) && Write() is { } error)
            {
                _log.LogWarning("The closed Recording {Recording} could not be removed from {Path}: {Message}; the next start reports it as interrupted", path, _path, error);
            }
        }
    }

    /// <summary>
    /// The Recordings the last run left open (FR-12), read without changing the file; empty when
    /// there are none or the file can't be read. Call it before anything opens a Recording, and
    /// <see cref="ClearInterrupted"/> once they have been reported.
    /// </summary>
    public IReadOnlyList<string> ReadInterrupted()
    {
        lock (_lock)
        {
            byte[] json;
            try
            {
                json = File.ReadAllBytes(_path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            var state = Parse(json);
            if (state?.OpenRecordings is not { Count: > 0 } open)
            {
                return [];
            }

            _log.LogWarning("{File} lists {Count} Recording(s) the last run left open", FileName, open.Count);
            _interrupted = true;
            _muted ??= state.MicMuted;
            return open;
        }
    }

    /// <summary>
    /// Rewrites the file without the Recordings <see cref="ReadInterrupted"/> found, keeping its mic
    /// mute; nothing happens when it found none. Call it once they have been reported.
    /// </summary>
    public void ClearInterrupted()
    {
        lock (_lock)
        {
            if (!_interrupted)
            {
                return;
            }

            _interrupted = false;
            if (Write() is { } error)
            {
                _log.LogWarning("The interrupted Recordings could not be cleared from {Path}: {Message}; they stay in it until the next successful write", _path, error);
            }
        }
    }

    private static RuntimeState? Parse(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.RuntimeState);
        }
        catch (JsonException)
        {
            return null;
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

    private bool Loaded(bool muted)
    {
        lock (_lock)
        {
            _muted = muted;
            return muted;
        }
    }

    /// <summary>Writes both values; the failure's message when the write failed, else null. Call under the lock.</summary>
    private string? Write()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new RuntimeState(_muted, _open.Count > 0 ? [.. _open] : null), RuntimeJsonContext.Default.RuntimeState);
                stream.Flush(flushToDisk: true);
            }

            File.Move(_tempPath, _path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(_tempPath);
            return e.Message;
        }
    }
}

/// <summary>
/// <c>runtime.json</c>'s shape; a missing <c>micMuted</c> makes the file unreadable for mic mute. A
/// null value is left out when writing, so <c>openRecordings</c> is there only while it isn't empty.
/// </summary>
internal sealed record RuntimeState(bool? MicMuted, IReadOnlyList<string>? OpenRecordings = null);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuntimeState))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext;
