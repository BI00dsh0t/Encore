namespace Encore.Core.Library;

/// <summary>Watches the Output Folder for the Library actor. Its callbacks run on the watcher's threads, so they must only post.</summary>
internal sealed class LibraryWatcher(
    Action<WatcherChangeTypes, string, string?> changed,
    Action<Exception> error,
    Action<Exception> failed)
{
    private const int BufferSize = 64 * 1024;

    private FileSystemWatcher? _watcher;
    private volatile bool _raising = true;

    /// <summary>Whether the watcher raises events; a test turns it off to stand in for missed events.</summary>
    public bool IsRaising => _raising;

    /// <summary>Watches a folder in place of the one watched before; a folder that doesn't exist isn't watched.</summary>
    public void Start(string folder)
    {
        Stop();
        if (!Directory.Exists(folder))
        {
            return;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folder, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = BufferSize,
            };
            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = _raising;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            failed(e);
        }
    }

    public void Stop()
    {
        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.Created -= OnFileEvent;
            watcher.Changed -= OnFileEvent;
            watcher.Deleted -= OnFileEvent;
            watcher.Renamed -= OnFileRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
    }

    public void SetRaising(bool on)
    {
        _raising = on;
        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = on;
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                failed(e);
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => changed(e.ChangeType, e.FullPath, null);

    private void OnFileRenamed(object sender, RenamedEventArgs e) => changed(e.ChangeType, e.FullPath, e.OldFullPath);

    private void OnError(object sender, ErrorEventArgs e) => error(e.GetException());
}
