using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Core.Library;

/// <summary>
/// The single-threaded Library index (AD-17): every index change and every <c>library.json</c> write
/// runs on its own thread, reading a single-reader channel; <see cref="BeginWrite"/> and
/// <see cref="EndWrite"/> only post. An item's key is its path relative to the Output Folder
/// (ordinal ignore-case); a write's lifecycle publishes <c>library.itemAdded</c> (locked) at its
/// start, then <c>library.itemChanged</c> with its size, or <c>library.itemRemoved</c> when it failed.
/// Items being written are never persisted.
/// </summary>
public sealed class LibraryActor : ILibraryWrites, IDisposable
{
    private const string Extension = ".mp4";

    private readonly Channel<Message> _channel = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly Func<string> _outputFolder;
    private readonly string _libraryFile;
    private readonly Action<LibraryItem> _added;
    private readonly Action<LibraryItem> _changed;
    private readonly Action<LibraryItemRemoved> _removed;
    private readonly TimeProvider _time;
    private readonly Action<Exception> _failed;
    private readonly Thread _thread;

    // Actor thread only.
    private readonly Dictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: each write under way, by absolute path (AD-9), to its item's key.
    private readonly Dictionary<string, string> _beingWritten = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    /// <param name="items">The items <see cref="Load"/> found; already in the first snapshot, so not published.</param>
    /// <param name="outputFolder">The current Output Folder.</param>
    /// <param name="libraryFile">The <c>library.json</c> path.</param>
    /// <param name="added">Publishes <c>library.itemAdded</c>.</param>
    /// <param name="changed">Publishes <c>library.itemChanged</c>.</param>
    /// <param name="removed">Publishes <c>library.itemRemoved</c>.</param>
    /// <param name="time">The clock a write's date is read from.</param>
    /// <param name="failed">Reports a <c>library.json</c> write that failed; the index is unaffected.</param>
    public LibraryActor(
        IReadOnlyList<LibraryItem> items,
        Func<string> outputFolder,
        string libraryFile,
        Action<LibraryItem> added,
        Action<LibraryItem> changed,
        Action<LibraryItemRemoved> removed,
        TimeProvider time,
        Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(libraryFile);
        _outputFolder = outputFolder ?? throw new ArgumentNullException(nameof(outputFolder));
        _libraryFile = libraryFile;
        _added = added ?? throw new ArgumentNullException(nameof(added));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _removed = removed ?? throw new ArgumentNullException(nameof(removed));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        foreach (var item in items)
        {
            _items[item.Path] = item;
        }

        _thread = new Thread(Run) { Name = "LibraryActor", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The actor thread's managed id.</summary>
    internal int ThreadId => _thread.ManagedThreadId;

    /// <summary>
    /// The Library at start: the Output Folder's non-empty <c>*.mp4</c> files, each with its
    /// <paramref name="libraryFile"/> entry's profile and date when it has one, else no profile and the
    /// file's creation time. Entries whose file is gone are dropped; an unreadable file is reported to
    /// <paramref name="failed"/> and the scan alone is used.
    /// </summary>
    public static IReadOnlyList<LibraryItem> Load(string outputFolder, string libraryFile, Action<Exception> failed)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        ArgumentException.ThrowIfNullOrEmpty(libraryFile);
        ArgumentNullException.ThrowIfNull(failed);

        var saved = ReadFile(libraryFile, failed);
        var folder = Path.GetFullPath(outputFolder);
        var items = new List<LibraryItem>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return items;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var file = new FileInfo(path);

                // An empty file is a reservation a write never filled (it ended with the engine).
                if (file.Length == 0)
                {
                    continue;
                }

                var key = Path.GetRelativePath(folder, file.FullName);
                items.Add(saved.TryGetValue(key, out var entry)
                    ? new LibraryItem(key, entry.ProfileId, file.Length, entry.Date, false)
                    : new LibraryItem(key, null, file.Length, new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero), false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed(e);
        }

        return items;
    }

    /// <inheritdoc/>
    public void BeginWrite(string path, string? profileId)
    {
        if (path is not null)
        {
            _channel.Writer.TryWrite(new Begin(path, profileId));
        }
    }

    /// <inheritdoc/>
    public void EndWrite(string path, WriteOutcome outcome)
    {
        if (path is not null)
        {
            _channel.Writer.TryWrite(new End(path, outcome));
        }
    }

    /// <summary>Stops taking messages, handles those already posted, and joins the thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }
    }

    private static Dictionary<string, LibraryFileEntry> ReadFile(string libraryFile, Action<Exception> failed)
    {
        var entries = new Dictionary<string, LibraryFileEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(libraryFile))
            {
                return entries;
            }

            var document = JsonSerializer.Deserialize(File.ReadAllBytes(libraryFile), LibraryFileJsonContext.Default.LibraryFile)
                ?? throw new JsonException("library.json is null.");
            // The deserializer doesn't check list elements, so a hand-edited null is skipped.
            foreach (var entry in document.Items.Where(e => e is not null))
            {
                entries[entry.Path] = entry;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // 7.2 adds library-corrupt; until then the scan alone is used.
            failed(e);
            entries.Clear();
        }

        return entries;
    }

    private void Run()
    {
        var reader = _channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var message))
            {
                try
                {
                    Handle(message);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _failed(e);
                }
            }
        }
    }

    private void Handle(Message message)
    {
        switch (message)
        {
            case Begin begin:
                OnBegin(begin);
                break;

            case End end:
                OnEnd(end);
                break;
        }
    }

    private void OnBegin(Begin begin)
    {
        var path = Path.GetFullPath(begin.Path);
        var folder = Path.GetFullPath(_outputFolder());
        var key = Path.GetRelativePath(folder, path);

        // A path outside the Output Folder isn't the Library's.
        if (Path.IsPathRooted(key) || key == ".." || key.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return;
        }

        _beingWritten[path] = key;
        var item = new LibraryItem(key, begin.ProfileId, 0, _time.GetUtcNow(), true);
        _items[key] = item;
        _added(item);
    }

    private void OnEnd(End end)
    {
        var path = Path.GetFullPath(end.Path);
        if (!_beingWritten.Remove(path, out var key) || !_items.TryGetValue(key, out var item))
        {
            return;
        }

        if (end.Outcome == WriteOutcome.Failed)
        {
            _items.Remove(key);
            _removed(new LibraryItemRemoved(key));
            return;
        }

        var file = new FileInfo(path);
        item = item with { Size = file.Exists ? file.Length : 0, BeingWritten = false };
        _items[key] = item;
        _changed(item);
        Save();
    }

    // Atomic: a temp file flushed to disk, then moved over library.json.
    private void Save()
    {
        var temp = _libraryFile + ".tmp";
        try
        {
            var document = new LibraryFile(
                1,
                [.. _items.Values.Where(i => !i.BeingWritten).Select(i => new LibraryFileEntry(i.Path, i.ProfileId, i.Date))]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_libraryFile))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    JsonSerializer.Serialize(writer, document, LibraryFileJsonContext.Default.LibraryFile);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _libraryFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The next save overwrites it.
            }

            _failed(e);
        }
    }

    private abstract record Message;

    private sealed record Begin(string Path, string? ProfileId) : Message;

    private sealed record End(string Path, WriteOutcome Outcome) : Message;
}

/// <summary><c>library.json</c>: the saved items, without their size, which is read from disk.</summary>
internal sealed record LibraryFile(int Version, IReadOnlyList<LibraryFileEntry> Items);

/// <summary>One saved item: its path relative to the Output Folder, its Game Profile and its date.</summary>
internal sealed record LibraryFileEntry(string Path, string? ProfileId, DateTimeOffset Date);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LibraryFile))]
internal sealed partial class LibraryFileJsonContext : JsonSerializerContext;
