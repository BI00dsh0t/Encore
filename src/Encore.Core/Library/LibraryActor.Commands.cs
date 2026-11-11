using System.Diagnostics.CodeAnalysis;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.Core.Library;

// The Library's commands: Favourite, rename and delete.
public sealed partial class LibraryActor
{
    private const int SharingRetries = 10;
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    // A rename or recycle that meets a sharing violation (e.g. the length worker's read) is tried again.
    private static readonly TimeSpan SharingRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Sets the Favourite when the command's identity matches the item and the file on disk; else refuses it.</summary>
    private void OnSetFavourite(SetFavouriteRequest request)
    {
        var args = request.Args;
        if (!TryMatch(args.Path, args.Size, args.Modified, request.Done, out var key, out var item))
        {
            return;
        }

        if (item.Favourite != args.Favourite)
        {
            _index.Change(item with { Favourite = args.Favourite });
        }

        request.Done.TrySetResult();
    }

    /// <summary>Whether a command's identity matches the item and the file on disk; a stale one is refused and refreshed.</summary>
    private bool TryMatch(
        string? path,
        long size,
        DateTimeOffset? modified,
        TaskCompletionSource done,
        out string key,
        out LibraryItem item)
    {
        if (!TryItem(path, out key, out var found)
            || found.Size != size
            || found.Modified != modified)
        {
            item = null!;
            Refuse(done, path);
            return false;
        }

        item = found;

        // Being written: the writer owns the file, so only the index is compared.
        if (item.BeingWritten || MatchesDisk(key, item))
        {
            return true;
        }

        Refuse(done, path);
        return false;
    }

    // Whether the file on disk is still the item's; a missing file is checked later, and a changed one refreshes the item.
    private bool MatchesDisk(string key, LibraryItem item)
    {
        var full = Path.Combine(_folder, key);
        var file = new FileInfo(full);
        if (!file.Exists)
        {
            _pending.Add(full);
            return false;
        }

        var onDisk = LibraryFiles.Utc(file.LastWriteTimeUtc);
        if (file.Length == item.Size && onDisk == item.Modified)
        {
            return true;
        }

        var resized = file.Length != item.Size;
        if (resized)
        {
            _index.ForgetLength(key);
        }

        _index.Change(item with { Size = file.Length, Modified = onDisk, LengthSeconds = resized ? null : item.LengthSeconds });
        if (resized)
        {
            QueueLength(key);
        }

        return false;
    }

    /// <summary>The indexed item a command's relative path names.</summary>
    private bool TryItem(string? path, out string key, [NotNullWhen(true)] out LibraryItem? item)
    {
        item = null;
        key = "";
        return !string.IsNullOrEmpty(path)
            && LibraryPaths.TryKey(_folder, Path.GetFullPath(Path.Combine(_folder, path)), out key)
            && _index.TryGet(key, out item);
    }

    /// <summary>Refuses a rename or delete of an item a writer is still writing; checked before the identity.</summary>
    private bool RefuseBeingWritten(string? path, TaskCompletionSource done)
    {
        if (!TryItem(path, out _, out var item) || !item.BeingWritten)
        {
            return false;
        }

        done.TrySetException(new ErrorCodeException(
            ErrorCodes.FileBeingWritten,
            $"The Library item '{path}' is still being written."));
        return true;
    }

    /// <summary>Renames a written item's file; both paths are registered first, so the watcher's echo is ignored.</summary>
    private void OnRename(RenameRequest request)
    {
        var args = request.Args;
        if (RefuseBeingWritten(args.Path, request.Done)
            || !TryMatch(args.Path, args.Size, args.Modified, request.Done, out var key, out var item))
        {
            return;
        }

        if (!LibraryPaths.ValidName(args.NewName))
        {
            request.Done.TrySetException(new ErrorCodeException(
                ErrorCodes.InvalidFileName,
                $"'{args.NewName}' can't be used as a file name."));
            return;
        }

        var name = args.NewName + Path.GetExtension(key);
        if (string.Equals(Path.GetFileName(key), name, StringComparison.Ordinal))
        {
            request.Done.TrySetResult();
            return;
        }

        RenameFile(request, key, item, name);
    }

    // Moves the file to its new name, once nothing else is there and the file is the Library's to change.
    private void RenameFile(RenameRequest request, string key, LibraryItem item, string name)
    {
        var newKey = Path.Combine(Path.GetDirectoryName(key) ?? "", name);
        var from = Path.Combine(_folder, key);
        var to = Path.Combine(_folder, newKey);

        // A case-only rename of the same file is allowed; any other file, item or write there isn't.
        var sameFile = string.Equals(newKey, key, StringComparison.OrdinalIgnoreCase);
        if (!sameFile && (_index.Contains(newKey) || _index.IsBeingWritten(to) || File.Exists(to) || Directory.Exists(to)))
        {
            request.Done.TrySetException(new ErrorCodeException(
                ErrorCodes.FileNameTaken,
                $"A file named '{name}' already exists there."));
            return;
        }

        if (!Inside(Path.GetFullPath(from)))
        {
            RefuseOutside(request.Done, request.Args.Path);
            return;
        }

        _ownOperations.Register(from);
        _ownOperations.Register(to);
        if (!Retried(() => File.Move(from, to), request.Done, "The file couldn't be renamed."))
        {
            return;
        }

        MoveItem(item, newKey);
        request.Done.TrySetResult();
    }

    /// <summary>Moves a written item's file to the Recycle Bin; its path is registered first, so the watcher's echo is ignored.</summary>
    private void OnDelete(DeleteRequest request)
    {
        var args = request.Args;
        if (RefuseBeingWritten(args.Path, request.Done)
            || !TryMatch(args.Path, args.Size, args.Modified, request.Done, out var key, out _))
        {
            return;
        }

        var path = Path.Combine(_folder, key);
        if (!Inside(Path.GetFullPath(path)))
        {
            RefuseOutside(request.Done, args.Path);
            return;
        }

        var recycled = false;
        if (_recycle is { } recycle)
        {
            _ownOperations.Register(path);
            if (!Retried(() => recycled = recycle(path), request.Done, "The file couldn't be deleted."))
            {
                return;
            }
        }

        if (!recycled)
        {
            request.Done.TrySetException(new ErrorCodeException(
                ErrorCodes.RecycleBinUnavailable,
                $"The drive of '{args.Path}' has no Recycle Bin, so it wasn't deleted."));
            return;
        }

        _index.Remove(key);
        request.Done.TrySetResult();
    }

    /// <summary>Runs a file operation, retrying a sharing violation up to 10 times, 50 ms apart; other failures refuse it.</summary>
    private bool Retried(Action operation, TaskCompletionSource done, string message)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return true;
            }
            catch (IOException e) when (e.HResult is ErrorSharingViolation or ErrorLockViolation && attempt < SharingRetries)
            {
                Thread.Sleep(SharingRetryDelay);
            }
#pragma warning disable CA1031 // Any failure, an injected port's included, refuses the command rather than ending the actor.
            catch (Exception e)
#pragma warning restore CA1031
            {
                _failed(e);
                done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, $"{message} {e.Message}"));
                return false;
            }
        }
    }

    private static void Refuse(TaskCompletionSource done, string? path) =>
        done.TrySetException(new ErrorCodeException(
            ErrorCodes.LibraryItemChanged,
            $"The Library item '{path}' changed since it was shown; act on it again."));

    /// <summary>Whether the actor may change the file: no junction or link on its path, and it is under the Output Folder.</summary>
    private bool Inside(string full)
    {
        if (!LibraryPaths.TryKey(_folder, full, out _) || LibraryPaths.ThroughReparsePoint(_folder, full))
        {
            return false;
        }

        if (_finalPath is null)
        {
            return true;
        }

        try
        {
            var root = _finalPath(_folder);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            return _finalPath(full).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031 // Any failure of the injected port means the file isn't touched.
        catch (Exception e)
#pragma warning restore CA1031
        {
            _failed(e);
            return false;
        }
    }

    // A rename or delete of a file whose real location may be outside the Output Folder.
    private static void RefuseOutside(TaskCompletionSource done, string? path) =>
        done.TrySetException(new ErrorCodeException(
            ErrorCodes.CommandFailed,
            $"The Library item '{path}' is reached through a junction or symbolic link, so it wasn't changed."));
}
