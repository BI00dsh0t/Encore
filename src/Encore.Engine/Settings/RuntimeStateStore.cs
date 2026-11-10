using System.Text.Json;
using Microsoft.Extensions.Logging;
using Encore.Core.Sessions;

namespace Encore.Engine.Settings;

/// <summary>The engine's runtime state in <c>runtime.json</c>: mic mute and open Recordings. Never a setting.</summary>
internal sealed class RuntimeStateStore : IMicMuteStore, IOpenRecordings
{
    public const string FileName = "runtime.json";

    private readonly ILogger _log;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly List<string> _open = [];
    private bool? _muted;
    private bool _interrupted;

    public RuntimeStateStore(string folder, ILogger log)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(log);
        _path = Path.Combine(Path.GetFullPath(folder), FileName);
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

    /// <summary>The Recordings the last run left open, read without changing the file. Call it before any Recording opens.</summary>
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

    /// <summary>Rewrites the file without the interrupted Recordings, keeping mic mute; call it once they are reported.</summary>
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
            AtomicFile.Replace(_path, stream => JsonSerializer.Serialize(stream, Snapshot(), RuntimeJsonContext.Default.RuntimeState));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private RuntimeState Snapshot() => new(_muted, _open.Count > 0 ? [.. _open] : null);
}
