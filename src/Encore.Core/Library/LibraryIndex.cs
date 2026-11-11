using System.Diagnostics.CodeAnalysis;
using Encore.Contracts.Messages;

namespace Encore.Core.Library;

/// <summary>The Library's items in memory and the rules for changing them; each change is published. Actor thread only.</summary>
internal sealed class LibraryIndex
{
    private readonly Action<LibraryItem> _added;
    private readonly Action<LibraryItem> _changed;
    private readonly Action<LibraryItemRemoved> _removed;
    private readonly Action _markUnsaved;

    // Each item, by its key: its path under the Output Folder.
    private readonly Dictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);

    // Each write under way, by absolute path, to its item's key.
    private readonly Dictionary<string, string> _beingWritten = new(StringComparer.OrdinalIgnoreCase);

    // The modified time (LastWriteTimeUtc) each item's length was read at, by key.
    private readonly Dictionary<string, DateTime> _lengthReadAt = new(StringComparer.OrdinalIgnoreCase);

    // The size each queued length read was queued at, by key, so a read isn't queued twice.
    private readonly Dictionary<string, long> _lengthQueuedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The callbacks publish each change; markUnsaved says the index file no longer matches.</summary>
    public LibraryIndex(
        IReadOnlyList<LibraryItem> items,
        Action<LibraryItem> added,
        Action<LibraryItem> changed,
        Action<LibraryItemRemoved> removed,
        Action markUnsaved)
    {
        _added = added;
        _changed = changed;
        _removed = removed;
        _markUnsaved = markUnsaved;
        foreach (var item in items)
        {
            _items[item.Path] = item;
        }
    }

    public IReadOnlyCollection<LibraryItem> Items => _items.Values;

    public IReadOnlyCollection<string> Keys => _items.Keys;

    public bool TryGet(string key, [NotNullWhen(true)] out LibraryItem? item) => _items.TryGetValue(key, out item);

    public bool Contains(string key) => _items.ContainsKey(key);

    /// <summary>Whether a writer is writing the file at this absolute path.</summary>
    public bool IsBeingWritten(string path) => _beingWritten.ContainsKey(path);

    /// <summary>Whether any item's key lies under the folder <paramref name="prefix"/>.</summary>
    public bool HasKeysUnder(string prefix)
    {
        var folder = prefix + Path.DirectorySeparatorChar;
        return _items.Keys.Any(key => key.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The written items under the folder <paramref name="prefix"/>.</summary>
    public List<LibraryItem> WrittenUnder(string prefix)
    {
        var folder = prefix + Path.DirectorySeparatorChar;
        return _items.Values
            .Where(item => !item.BeingWritten && item.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>A write begins: its item is listed at once, locked. It replaces an item already at that key.</summary>
    public void BeginWrite(string path, LibraryItem item)
    {
        _beingWritten[path] = item.Path;

        // A write over an indexed file: a length read still queued for the old file is the old file's.
        _lengthReadAt.Remove(item.Path);
        _lengthQueuedAt.Remove(item.Path);
        _items[item.Path] = item;
        _added(item);
    }

    /// <summary>A write ended: false when the path isn't a write under way, or its item is gone.</summary>
    public bool TryEndWrite(string path, [NotNullWhen(true)] out LibraryItem? item)
    {
        item = null;
        return _beingWritten.Remove(path, out var key) && _items.TryGetValue(key, out item);
    }

    /// <summary>A write that failed leaves no item; nothing was saved for it, so nothing needs saving.</summary>
    public void DropFailedWrite(string key)
    {
        _items.Remove(key);
        _removed(new LibraryItemRemoved(key));
    }

    /// <summary>Lists a new item.</summary>
    public void Add(LibraryItem item)
    {
        _items[item.Path] = item;
        _added(item);
        _markUnsaved();
    }

    /// <summary>Replaces an item with a changed copy of itself.</summary>
    public void Change(LibraryItem item)
    {
        _items[item.Path] = item;
        _changed(item);
        _markUnsaved();
    }

    /// <summary>The item's file moved: it leaves its old key and is listed under the new one, keeping its length.</summary>
    public LibraryItem Move(LibraryItem item, string newKey)
    {
        _items.Remove(item.Path);
        _removed(new LibraryItemRemoved(item.Path));
        var moved = item with { Path = newKey };
        _items[newKey] = moved;
        _added(moved);
        _markUnsaved();

        // A length read queued under the old key is asked again under the new one.
        _lengthQueuedAt.Remove(item.Path);
        if (_lengthReadAt.Remove(item.Path, out var readAt))
        {
            _lengthReadAt[newKey] = readAt;
        }

        return moved;
    }

    /// <summary>Removes the item at a key, if there is one, with what it knows about its length.</summary>
    public void Remove(string key, string? reason = null)
    {
        _lengthReadAt.Remove(key);
        _lengthQueuedAt.Remove(key);
        if (_items.Remove(key))
        {
            _removed(new LibraryItemRemoved(key, reason));
            _markUnsaved();
        }
    }

    /// <summary>Forgets everything: the Output Folder changed.</summary>
    public void Clear()
    {
        _items.Clear();
        _beingWritten.Clear();
        _lengthReadAt.Clear();
        _lengthQueuedAt.Clear();
    }

    /// <summary>Sets an item without publishing it; the caller publishes the whole index after.</summary>
    public void Restore(LibraryItem item) => _items[item.Path] = item;

    /// <summary>The item's file was rewritten: its length no longer belongs to it.</summary>
    public void ForgetLength(string key) => _lengthReadAt.Remove(key);

    /// <summary>A length loaded with the item belongs to the file as it was modified at this time.</summary>
    public void RememberLengthReadAt(string key, DateTime modified) => _lengthReadAt[key] = modified;

    /// <summary>The item whose length should be read: it is written, and no read is queued for it at its size.</summary>
    public bool NeedsLength(string key, [NotNullWhen(true)] out LibraryItem? item) =>
        _items.TryGetValue(key, out item)
        && !item.BeingWritten
        && !(_lengthQueuedAt.TryGetValue(key, out var size) && size == item.Size);

    public void LengthQueued(string key, long size) => _lengthQueuedAt[key] = size;

    /// <summary>A length read came back: it is applied only when the item is still written and still that size.</summary>
    public void ApplyLength(string key, long size, DateTime modified, double? length)
    {
        if (_lengthQueuedAt.TryGetValue(key, out var queued) && queued == size)
        {
            _lengthQueuedAt.Remove(key);
        }

        if (length is not { } seconds
            || !_items.TryGetValue(key, out var item)
            || item.BeingWritten
            || item.Size != size)
        {
            return;
        }

        item = item with { LengthSeconds = seconds };
        _items[key] = item;
        _lengthReadAt[key] = modified;
        _changed(item);
        _markUnsaved();
    }

    /// <summary>What the index file saves: every written item.</summary>
    public IReadOnlyList<LibraryFileEntry> Entries() => [.. _items.Values.Where(item => !item.BeingWritten).Select(Entry)];

    // A length is saved with the modified time it was read at, so a restart can tell it still holds.
    private LibraryFileEntry Entry(LibraryItem item) =>
        item.LengthSeconds is { } length && _lengthReadAt.TryGetValue(item.Path, out var modified)
            ? new LibraryFileEntry(item.Path, item.ProfileId, item.Date, item.Size, length, modified, item.Favourite, item.Kind)
            : new LibraryFileEntry(item.Path, item.ProfileId, item.Date, item.Size, Favourite: item.Favourite, Kind: item.Kind);
}
