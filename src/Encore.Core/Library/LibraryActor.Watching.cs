using Encore.Contracts.Messages;

namespace Encore.Core.Library;

// What the watcher saw: each path it named is checked against disk once its events settle.
public sealed partial class LibraryActor
{
    // An incomplete file written this recently may still be growing, so it is checked again.
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);

    private void OnWatched(Watched watched)
    {
        if (watched.OldPath is { } old)
        {
            OnRenamed(old, watched.Path);
            return;
        }

        var path = watched.Path;
        if (!LibraryPaths.TryKey(_folder, path, out var key) || Ignored(path))
        {
            return;
        }

        // A folder moved or copied in reports only itself; a folder deleted reports only itself.
        if ((watched.Kind == WatcherChangeTypes.Created && Directory.Exists(path))
            || (watched.Kind == WatcherChangeTypes.Deleted && !LibraryPaths.IsMp4(path) && _index.HasKeysUnder(key)))
        {
            _pending.AddRescan();
            return;
        }

        _pending.Add(path);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        // The writer's .rbpart renamed over its reservation, or the echo of the actor's own move.
        if (Ignored(oldPath) || Ignored(newPath))
        {
            return;
        }

        var oldIn = LibraryPaths.TryKey(_folder, oldPath, out var oldKey);
        var newIn = LibraryPaths.TryKey(_folder, newPath, out var newKey);
        if (newIn && Directory.Exists(newPath))
        {
            if (oldIn)
            {
                MoveKeysUnder(oldKey, newKey);
            }

            _pending.AddRescan();
            return;
        }

        if (oldIn)
        {
            if (newIn && LibraryPaths.IsMp4(newPath) && _index.TryGet(oldKey, out var item) && !item.BeingWritten)
            {
                MoveItem(item, newKey);
            }
            else if (!LibraryPaths.IsMp4(oldPath) && _index.HasKeysUnder(oldKey))
            {
                _pending.AddRescan();
            }
            else
            {
                _pending.Add(oldPath);
            }
        }

        if (newIn)
        {
            _pending.Add(newPath);
        }
    }

    private void OnSettle()
    {
        _pending.Settled();
        if (_pending.RescanPending)
        {
            RescanNow();
            return;
        }

        foreach (var path in _pending.TakePaths())
        {
            Reconcile(path);
        }
    }

    private void OnWatchError(WatchError error)
    {
        _failed(error.Error);
        CreateMissingFolder();
        _watcher.Start(_folder);
        RescanNow();
    }

    // A pending rescan, or a watcher restart, rescans the whole folder; its end may start a Cleanup pass.
    private void RescanNow()
    {
        _pending.Clear();
        Rescan();
        StartCleanup();
    }

    private void OnExpired(Expired expired)
    {
        if (_ownOperations.Expire(expired.Path, expired.Id))
        {
            Reconcile(expired.Path);
        }
    }

    private void OnFolderChanged()
    {
        var folder = LibraryPaths.FullFolder(_outputFolder());
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folder = folder;
        _index.Clear();
        _pending.Clear();
        _pass.Skipped.Clear();
        _ownOperations.Clear();
        CreateMissingFolder();

        // Watched before the scan, so a file created meanwhile is reconciled once it settles.
        _watcher.Start(_folder);
        foreach (var path in LibraryFiles.Files(folder, LibraryPaths.Extension, _failed))
        {
            if (LibraryPaths.TryKey(folder, path, out var key) && Check(path) is { } file)
            {
                _index.Restore(LibraryFiles.Scanned(file, key, _profileForFolder));
            }
        }

        _folderChanged?.Invoke(new LibraryFolderChanged(folder));
        _reset(new LibraryReset([.. _index.Items]));
        _dirty = true;
        foreach (var key in _index.Keys.ToList())
        {
            QueueLength(key);
        }
    }

    private void OnWatch(Watch watch)
    {
        _watcher.SetRaising(watch.On);
        watch.Done.TrySetResult();
    }

    /// <summary>Checks one path against disk: removes, keeps or lists it; a busy or incomplete file is checked again later.</summary>
    private void Reconcile(string path)
    {
        if (!LibraryPaths.TryKey(_folder, path, out var key) || Ignored(path))
        {
            return;
        }

        var file = new FileInfo(path);
        if (!file.Exists || !LibraryPaths.IsMp4(path) || file.Length == 0 || LibraryPaths.ThroughReparsePoint(_folder, path))
        {
            _index.Remove(key);
            return;
        }

        if (_index.TryGet(key, out var item) && item.Size == file.Length)
        {
            return;
        }

        if (Check(path) is null)
        {
            // A path Check pended for another look keeps its item, and its profile and date, until then.
            if (!_pending.Contains(path))
            {
                _index.Remove(key);
            }

            return;
        }

        if (item is null)
        {
            _index.Add(LibraryFiles.Scanned(file, key, _profileForFolder));
        }
        else
        {
            // A file rewritten outside the app has a new length.
            _index.ForgetLength(key);
            _index.Change(item with { Size = file.Length, Modified = LibraryFiles.Utc(file.LastWriteTimeUtc), LengthSeconds = null });
        }

        QueueLength(key);
    }

    /// <summary>The file when it is a non-empty, complete MP4; else null, with a busy or fresh file queued for another check.</summary>
    private FileInfo? Check(string path)
    {
        var file = new FileInfo(path);
        try
        {
            if (!file.Exists || file.Length == 0)
            {
                return null;
            }

            if (Mp4File.IsComplete(path))
            {
                return file;
            }

            // The file system's clock, not the injected one: LastWriteTimeUtc comes from it.
            if (DateTime.UtcNow - file.LastWriteTimeUtc < RecentWrite)
            {
                _pending.Add(path);
            }
        }
        catch (IOException)
        {
            // Locked, e.g. a copy still running.
            _pending.Add(path);
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>The union of the indexed keys and the files on disk, each reconciled.</summary>
    private void Rescan()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _index.Keys)
        {
            paths.Add(Path.Combine(_folder, key));
        }

        paths.UnionWith(LibraryFiles.Files(_folder, LibraryPaths.Extension, _failed));
        foreach (var path in paths)
        {
            Reconcile(path);
        }
    }

    /// <summary>A write under way, its <c>.rbpart</c>, or a path of the actor's own operation not yet expired.</summary>
    private bool Ignored(string path) =>
        _index.IsBeingWritten(path)
        || (path.EndsWith(LibraryPaths.PartExtension, StringComparison.OrdinalIgnoreCase)
            && _index.IsBeingWritten(path[..^LibraryPaths.PartExtension.Length]))
        || _ownOperations.Covers(path);

    // A folder renamed: each item under it moves, keeping its profile and date.
    private void MoveKeysUnder(string oldPrefix, string newPrefix)
    {
        foreach (var item in _index.WrittenUnder(oldPrefix))
        {
            MoveItem(item, newPrefix + item.Path[oldPrefix.Length..]);
        }
    }

    // A move keeps the length; one not yet read is read under the new key.
    private void MoveItem(LibraryItem item, string newKey)
    {
        if (_index.Move(item, newKey).LengthSeconds is null)
        {
            QueueLength(newKey);
        }
    }

    private void CreateMissingFolder()
    {
        if (_createMissingFolder && !Directory.Exists(_folder))
        {
            try
            {
                Directory.CreateDirectory(_folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _failed(e);
            }
        }
    }
}
