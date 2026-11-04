using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Core.Library;

/// <summary>What <see cref="LibraryActor.Load"/> found at start.</summary>
/// <param name="Items">The Library's items.</param>
/// <param name="Corrupt">Whether <c>library.json</c> and its <c>.bak</c> were both unreadable, so the items come from the scan alone, or an earlier rebuild is still unacknowledged; Cleanup stays blocked either way.</param>
public sealed record LibraryLoad(IReadOnlyList<LibraryItem> Items, bool Corrupt);

/// <summary>
/// The single-threaded Library index (AD-17): every index change, every disk check and every
/// <c>library.json</c> write runs on its own thread, reading a single-reader channel; the public
/// members, the timers and the <see cref="FileSystemWatcher"/> over the Output Folder only post. An
/// item's key is its path relative to the Output Folder (ordinal ignore-case). A write's lifecycle
/// publishes <c>library.itemAdded</c> (locked) at its start, then <c>library.itemChanged</c> with its
/// size, or <c>library.itemRemoved</c> when it failed. Changes made outside the app are reconciled
/// against disk (R2), so only what differs from the index is published. Items being written are never
/// persisted. Each item's length (story 7.3) is read by a low-priority worker beside the actor, which
/// only reads the file and posts the result back; the actor applies it, publishes it as
/// <c>library.itemChanged</c> and caches it in <c>library.json</c> by size and modified time. The App's
/// rename and delete (story 7.7) run here too, by the item's identity (AD-3): never on a file being
/// written, and each path they touch is registered first, so the watcher's echo isn't an outside change.
/// A delete only ever moves the file to the Recycle Bin, through the injected port.
/// </summary>
public sealed class LibraryActor : ILibraryWrites, IDisposable
{
    /// <summary>What a Clip is written to before its rename (the writer's <c>ClipWriter.PartExtension</c>).</summary>
    internal const string PartExtension = ".rbpart";

    private const string Extension = ".mp4";
    private const string BakExtension = ".bak";
    private const string CorruptExtension = ".corrupt";
    private const int WatcherBufferSize = 64 * 1024;
    private const int SharingRetries = 10;
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    // How long watcher events gather before their paths are reconciled, and the retry of a busy file.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    // An incomplete file written this recently may still be growing, so it is checked again.
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);

    // How long the watcher's echo of the actor's own file operation is ignored.
    private static readonly TimeSpan RegistrationTime = TimeSpan.FromSeconds(5);

    // A rename or recycle that meets a sharing violation (e.g. the length worker's read) is tried again.
    private static readonly TimeSpan SharingRetryDelay = TimeSpan.FromMilliseconds(50);

    // Device names Windows reserves, whatever the extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        AttributesToSkip = 0,
    };

    private readonly Channel<Message> _channel = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly Func<string> _outputFolder;
    private readonly string _libraryFile;
    private readonly Func<string, string?> _profileForFolder;
    private readonly Action<LibraryItem> _added;
    private readonly Action<LibraryItem> _changed;
    private readonly Action<LibraryItemRemoved> _removed;
    private readonly Action<LibraryReset> _reset;
    private readonly Action<LibraryCleanupBlocked> _cleanupBlockedChanged;
    private readonly TimeProvider _time;
    private readonly Action<Exception> _failed;
    private readonly bool _createMissingFolder;
    private readonly Func<string, double?>? _readLength;
    private readonly Action<LibraryFolderChanged>? _folderChanged;
    private readonly Func<string, bool>? _recycle;
    private readonly Thread _thread;

    // The length worker's jobs; written by the actor thread only.
    private readonly Channel<LengthJob> _lengthJobs = Channel.CreateUnbounded<LengthJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });

    private readonly Thread? _lengthThread;

    // Actor thread only.
    private readonly Dictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: each write under way, by absolute path (AD-9), to its item's key.
    private readonly Dictionary<string, string> _beingWritten = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the paths of the actor's own file operations, by absolute path, to their expiry.
    private readonly Dictionary<string, Registration> _registered = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the absolute paths watcher events named, reconciled once they settle.
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the modified time (LastWriteTimeUtc) each item's length was read at, by key.
    private readonly Dictionary<string, DateTime> _modified = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the size each queued length job was queued at, by key, so a job isn't queued twice.
    private readonly Dictionary<string, long> _queued = new(StringComparer.OrdinalIgnoreCase);

    private string _folder = "";
    private FileSystemWatcher? _watcher;
    private ITimer? _settle;
    private bool _settleArmed;
    private bool _rescanPending;
    private bool _cleanupBlocked;
    private bool _dirty;
    private long _registrations;
    private volatile bool _watching = true;
    private int _disposed;

    /// <param name="items">The items <see cref="Load"/> found; already in the first snapshot, so not published.</param>
    /// <param name="outputFolder">The current Output Folder, re-read by <see cref="OutputFolderChanged"/>.</param>
    /// <param name="libraryFile">The <c>library.json</c> path.</param>
    /// <param name="profileForFolder">The Game Profile whose folder is a top-level folder's name, or null; for files found on disk.</param>
    /// <param name="cleanupBlocked">Whether the load was corrupt, so Cleanup stays off until <see cref="AcknowledgeCorrupt"/>.</param>
    /// <param name="added">Publishes <c>library.itemAdded</c>.</param>
    /// <param name="changed">Publishes <c>library.itemChanged</c>.</param>
    /// <param name="removed">Publishes <c>library.itemRemoved</c>.</param>
    /// <param name="reset">Publishes <c>library.reset</c>.</param>
    /// <param name="cleanupBlockedChanged">Publishes <c>library.cleanupBlockedChanged</c>.</param>
    /// <param name="time">The clock a write's date is read from, and the settle timers' source.</param>
    /// <param name="failed">Reports a disk operation that failed; the index is unaffected.</param>
    /// <param name="createMissingFolder">Whether a missing Output Folder is created so it can be watched.</param>
    /// <param name="readLength">Reads a file's length in seconds by its absolute path, or null when it can't; called on a low-priority worker. Without it no length is read.</param>
    /// <param name="folderChanged">Publishes <c>library.folderChanged</c>, just before the <c>library.reset</c> of a new Output Folder.</param>
    /// <param name="recycle">Moves a file to the Recycle Bin by its absolute path, on the actor thread: false when its volume has none, an <see cref="IOException"/> carrying the HRESULT when it fails. Without it every delete is refused with <see cref="ErrorCodes.RecycleBinUnavailable"/>.</param>
    public LibraryActor(
        IReadOnlyList<LibraryItem> items,
        Func<string> outputFolder,
        string libraryFile,
        Func<string, string?> profileForFolder,
        bool cleanupBlocked,
        Action<LibraryItem> added,
        Action<LibraryItem> changed,
        Action<LibraryItemRemoved> removed,
        Action<LibraryReset> reset,
        Action<LibraryCleanupBlocked> cleanupBlockedChanged,
        TimeProvider time,
        Action<Exception> failed,
        bool createMissingFolder,
        Func<string, double?>? readLength = null,
        Action<LibraryFolderChanged>? folderChanged = null,
        Func<string, bool>? recycle = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(libraryFile);
        _outputFolder = outputFolder ?? throw new ArgumentNullException(nameof(outputFolder));
        _libraryFile = libraryFile;
        _profileForFolder = profileForFolder ?? throw new ArgumentNullException(nameof(profileForFolder));
        _added = added ?? throw new ArgumentNullException(nameof(added));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _removed = removed ?? throw new ArgumentNullException(nameof(removed));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _cleanupBlockedChanged = cleanupBlockedChanged ?? throw new ArgumentNullException(nameof(cleanupBlockedChanged));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _createMissingFolder = createMissingFolder;
        _readLength = readLength;
        _folderChanged = folderChanged;
        _recycle = recycle;
        _cleanupBlocked = cleanupBlocked;
        foreach (var item in items)
        {
            _items[item.Path] = item;
        }

        _channel.Writer.TryWrite(new Start());
        _thread = new Thread(Run) { Name = "LibraryActor", IsBackground = true };
        if (readLength is not null)
        {
            _lengthThread = new Thread(RunLengths) { Name = "LibraryLength", IsBackground = true, Priority = ThreadPriority.Lowest };
            _lengthThread.Start();
        }

        _thread.Start();
    }

    /// <summary>The actor thread's managed id.</summary>
    internal int ThreadId => _thread.ManagedThreadId;

    /// <summary>
    /// A test seam: whether the watcher raises events. Setting it waits until the actor has applied it,
    /// so changes made afterwards aren't seen until it is set again; a watcher restart keeps the setting.
    /// </summary>
    internal bool Watching
    {
        get => _watching;
        set
        {
            if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            {
                SetWatching(value);
                return;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_channel.Writer.TryWrite(new Watch(value, done)))
            {
                done.Task.Wait();
            }
        }
    }

    /// <summary>
    /// The Library at start. It deletes a crash's leftovers (each <c>*.rbpart</c>, and an empty
    /// <c>.mp4</c> beside one), reads <paramref name="libraryFile"/>, else its <c>.bak</c> (a
    /// <c>library.json</c> that doesn't parse is moved to <c>library.json.corrupt</c>), and ignores
    /// entries saved for a different Output Folder. It lists each non-empty <c>*.mp4</c> with its saved
    /// profile and date when its size still matches its entry, and any other only when it is a
    /// complete MP4, with the profile of its top-level folder and its creation time. Failures are
    /// reported to <paramref name="failed"/>.
    /// </summary>
    public static LibraryLoad Load(string outputFolder, string libraryFile, Func<string, string?> profileForFolder, Action<Exception> failed)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        ArgumentException.ThrowIfNullOrEmpty(libraryFile);
        ArgumentNullException.ThrowIfNull(profileForFolder);
        ArgumentNullException.ThrowIfNull(failed);

        var folder = FullFolder(outputFolder);
        var (saved, corrupt) = ReadIndex(libraryFile, folder, failed);
        var items = new List<LibraryItem>();
        if (!Directory.Exists(folder))
        {
            return new LibraryLoad(items, corrupt);
        }

        DeleteLeftovers(folder, failed);
        foreach (var path in Files(folder, Extension, failed))
        {
            if (!TryKey(folder, path, out var key))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(path);

                // An empty file is a reservation a write never filled (it ended with the engine).
                if (file.Length == 0)
                {
                    continue;
                }

                // A file the index doesn't know at this size must be a complete MP4; a 7.1 entry has no size.
                saved.TryGetValue(key, out var entry);
                var known = entry is not null && (entry.Size is null || entry.Size == file.Length);
                if (!known && !CompleteNow(path))
                {
                    continue;
                }

                // A cached length holds only while the file has the size and modified time it was read at.
                var length = entry is { LengthSeconds: { } seconds, Size: { } size, Modified: { } modified }
                    && size == file.Length && modified == file.LastWriteTimeUtc
                        ? seconds
                        : (double?)null;
                items.Add(entry is not null
                    ? new LibraryItem(key, entry.ProfileId, file.Length, entry.Date, false, length, entry.Favourite, Utc(file.LastWriteTimeUtc))
                    : Scanned(file, key, profileForFolder));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Gone or unreadable since the enumeration; the actor's start rescan checks it again.
            }
        }

        return new LibraryLoad(items, corrupt);
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

    /// <summary>
    /// <c>library.setFavourite</c> (story 7.4): sets the item's Favourite when the identity (AD-3) in
    /// <paramref name="args"/> matches the item and, unless it is being written, the file on disk;
    /// publishes <c>library.itemChanged</c> and saves it. Faults with <see cref="ErrorCodes.LibraryItemChanged"/>
    /// when the identity is stale (a file that changed on disk is refreshed and published first), and
    /// with <see cref="ErrorCodes.EngineDisconnected"/> once the actor is disposed.
    /// </summary>
    public Task SetFavourite(LibrarySetFavourite args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post(done => new SetFavouriteRequest(args, done));
    }

    /// <summary>
    /// <c>library.rename</c> (story 7.7): renames the item's file to <see cref="LibraryRename.NewName"/>
    /// plus <c>.mp4</c> in the same folder, and publishes exactly <c>library.itemRemoved</c> and
    /// <c>library.itemAdded</c>, keeping its metadata. Faults with <see cref="ErrorCodes.FileBeingWritten"/>,
    /// <see cref="ErrorCodes.LibraryItemChanged"/>, <see cref="ErrorCodes.InvalidFileName"/>,
    /// <see cref="ErrorCodes.FileNameTaken"/> or <see cref="ErrorCodes.CommandFailed"/>, the index unchanged.
    /// </summary>
    public Task Rename(LibraryRename args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post(done => new RenameRequest(args, done));
    }

    /// <summary>
    /// <c>library.delete</c> (story 7.7): moves the item's file to the Recycle Bin, publishes
    /// <c>library.itemRemoved</c> and saves. Faults with <see cref="ErrorCodes.FileBeingWritten"/>,
    /// <see cref="ErrorCodes.LibraryItemChanged"/>, <see cref="ErrorCodes.RecycleBinUnavailable"/> (the file
    /// untouched) or <see cref="ErrorCodes.CommandFailed"/>, the index unchanged.
    /// </summary>
    public Task Delete(LibraryDelete args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post(done => new DeleteRequest(args, done));
    }

    /// <summary>
    /// The settings changed: when the Output Folder is now a different folder, the index is rebuilt from
    /// it (the scan only, without saved metadata), published as <c>library.reset</c>, saved and watched.
    /// </summary>
    public void OutputFolderChanged() => _channel.Writer.TryWrite(new FolderChanged());

    /// <summary>The <c>library-corrupt</c> warning was dismissed: Cleanup is no longer blocked.</summary>
    public void AcknowledgeCorrupt() => _channel.Writer.TryWrite(new Acknowledge());

    /// <summary>
    /// The actor's own rename of <paramref name="from"/> to <paramref name="to"/>, both under the Output
    /// Folder: publishes exactly <c>library.itemRemoved</c> and <c>library.itemAdded</c>, keeping the
    /// item's profile and date; the watcher's echo is ignored. A move that fails is reported to the
    /// failure callback and leaves the index unchanged.
    /// </summary>
    internal void Move(string from, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        _channel.Writer.TryWrite(new MoveFile(from, to));
    }

    /// <summary>The watcher's <see cref="FileSystemWatcher.Error"/> handler: a buffer overflow, or the folder went away.</summary>
    internal void OnWatcherError(Exception error) => _channel.Writer.TryWrite(new WatchError(error));

    /// <summary>
    /// Stops taking messages, handles those already posted, and joins the thread; then stops the length
    /// worker, which skips the jobs still queued, and joins it.
    /// </summary>
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

        _lengthJobs.Writer.TryComplete();
        if (_lengthThread is not null && Environment.CurrentManagedThreadId != _lengthThread.ManagedThreadId)
        {
            _lengthThread.Join();
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be a file's base name: not empty, no character Windows forbids,
    /// not ending with a dot or a space, and not a reserved device name before its first dot.
    /// </summary>
    internal static bool ValidName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.')
            || name.EndsWith(' '))
        {
            return false;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return !ReservedNames.Contains(dot < 0 ? name : name[..dot]);
    }

    private static string FullFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static bool IsMp4(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The key of <paramref name="path"/> under <paramref name="folder"/>; false for a path outside it, or the folder itself.</summary>
    private static bool TryKey(string folder, string path, out string key)
    {
        key = Path.GetRelativePath(folder, path);
        return key != "."
            && key != ".."
            && !Path.IsPathRooted(key)
            && !key.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>A file found on disk: the profile of its top-level folder, and its creation time.</summary>
    private static LibraryItem Scanned(FileInfo file, string key, Func<string, string?> profileForFolder)
    {
        var separator = key.IndexOf(Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var profileId = separator > 0 ? profileForFolder(key[..separator]) : null;
        return new LibraryItem(key, profileId, file.Length, new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero), false, Modified: Utc(file.LastWriteTimeUtc));
    }

    /// <summary>A file's <see cref="FileSystemInfo.LastWriteTimeUtc"/> as an item's <see cref="LibraryItem.Modified"/>.</summary>
    private static DateTimeOffset Utc(DateTime lastWriteUtc) => new(lastWriteUtc, TimeSpan.Zero);

    /// <summary>Whether the file is a complete MP4 now; a busy or unreadable file isn't.</summary>
    private static bool CompleteNow(string path)
    {
        try
        {
            return Mp4File.IsComplete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The files under <paramref name="folder"/> with <paramref name="extension"/>; an enumeration that fails is reported, keeping what it found.</summary>
    private static List<string> Files(string folder, string extension, Action<Exception> failed)
    {
        var files = new List<string>();
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*" + extension, Recursive))
                {
                    // The pattern alone would also match a longer extension on some file systems.
                    if (string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed(e);
        }

        return files;
    }

    /// <summary>A crash's leftovers: each <c>*.rbpart</c>, and the empty reservation beside one. Never a user's <c>.mp4</c>.</summary>
    private static void DeleteLeftovers(string folder, Action<Exception> failed)
    {
        foreach (var part in Files(folder, PartExtension, failed))
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed(e);
            }

            try
            {
                var reservation = new FileInfo(part[..^PartExtension.Length]);
                if (IsMp4(reservation.FullName) && reservation.Exists && reservation.Length == 0)
                {
                    reservation.Delete();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed(e);
            }
        }
    }

    /// <summary>
    /// The saved entries: <c>library.json</c>, else its <c>.bak</c>. Corrupt when neither parses and at
    /// least one exists. Entries saved for a different Output Folder are ignored.
    /// </summary>
    private static (Dictionary<string, LibraryFileEntry> Entries, bool Corrupt) ReadIndex(string libraryFile, string folder, Action<Exception> failed)
    {
        var entries = new Dictionary<string, LibraryFileEntry>(StringComparer.OrdinalIgnoreCase);
        var main = TryRead(libraryFile, failed, out var document);
        var corrupt = false;
        if (main == ReadResult.Bad)
        {
            try
            {
                File.Move(libraryFile, libraryFile + CorruptExtension, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed(e);
            }
        }

        if (main != ReadResult.Read)
        {
            var bak = TryRead(libraryFile + BakExtension, failed, out document);
            corrupt = bak != ReadResult.Read && (main == ReadResult.Bad || bak == ReadResult.Bad);
        }

        // A rebuild not yet acknowledged keeps Cleanup blocked across a restart.
        corrupt |= document?.CleanupBlocked == true;
        if (document is null || (document.OutputFolder is { } saved && !SameFolder(saved, folder)))
        {
            return (entries, corrupt);
        }

        // The deserializer doesn't check list elements, so a hand-edited null is skipped.
        foreach (var entry in document.Items.Where(e => e is not null))
        {
            entries[entry.Path] = entry;
        }

        return (entries, corrupt);
    }

    private static bool SameFolder(string saved, string folder)
    {
        try
        {
            return string.Equals(FullFolder(saved), folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ReadResult TryRead(string path, Action<Exception> failed, out LibraryFile? document)
    {
        document = null;
        try
        {
            if (!File.Exists(path))
            {
                return ReadResult.Missing;
            }

            document = JsonSerializer.Deserialize(File.ReadAllBytes(path), LibraryFileJsonContext.Default.LibraryFile)
                ?? throw new JsonException($"{Path.GetFileName(path)} is null.");
            return ReadResult.Read;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            failed(e);
            document = null;
            return ReadResult.Bad;
        }
    }

    // A command's request, answered by the actor; faulted at once once the actor is disposed.
    private Task Post(Func<TaskCompletionSource, Message> request)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(request(done)))
        {
            done.TrySetException(new ErrorCodeException(ErrorCodes.EngineDisconnected, "The Library has shut down."));
        }

        return done.Task;
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

            // One save for each batch of published changes.
            if (_dirty)
            {
                _dirty = false;
                Save();
            }
        }

        StopWatcher();
        _settle?.Dispose();
        foreach (var registration in _registered.Values)
        {
            registration.Timer.Dispose();
        }
    }

    // The length worker: it only reads a file and posts the result to the actor (AD-17).
    private void RunLengths()
    {
        var reader = _lengthJobs.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var job))
            {
                // Once disposed, the actor would drop the result.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    continue;
                }

                double? length = null;
                var modified = default(DateTime);
                try
                {
                    modified = File.GetLastWriteTimeUtc(job.Path);
                    length = _readLength!(job.Path);
                }
#pragma warning disable CA1031 // A reader that throws only costs the item its length.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    _failed(e);
                }

                _channel.Writer.TryWrite(new LengthRead(job.Folder, job.Key, job.Size, modified, length));
            }
        }
    }

    private void Handle(Message message)
    {
        switch (message)
        {
            case Start:
                OnStart();
                break;

            case Begin begin:
                OnBegin(begin);
                break;

            case End end:
                OnEnd(end);
                break;

            case Watched watched:
                OnWatched(watched);
                break;

            case Settle:
                OnSettle();
                break;

            case WatchError error:
                OnWatchError(error);
                break;

            case Expired expired:
                OnExpired(expired);
                break;

            case FolderChanged:
                OnFolderChanged();
                break;

            case Acknowledge:
                OnAcknowledge();
                break;

            case MoveFile move:
                OnMove(move);
                break;

            case Watch watch:
                SetWatching(watch.On);
                watch.Done.TrySetResult();
                break;

            case LengthRead read:
                OnLengthRead(read);
                break;

            case SetFavouriteRequest request:
                try
                {
                    OnSetFavourite(request);
                }
                finally
                {
                    // A no-op once answered; an unexpected failure (reported by Run) mustn't leave the router waiting.
                    request.Done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, "The Favourite couldn't be set."));
                }

                break;

            case RenameRequest request:
                try
                {
                    OnRename(request);
                }
                finally
                {
                    request.Done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, "The file couldn't be renamed."));
                }

                break;

            case DeleteRequest request:
                try
                {
                    OnDelete(request);
                }
                finally
                {
                    request.Done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, "The file couldn't be deleted."));
                }

                break;
        }
    }

    // The gap between Load and the watcher is covered by one rescan; a corrupt load is saved at once.
    private void OnStart()
    {
        _settle = _time.CreateTimer(_ => _channel.Writer.TryWrite(new Settle()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _folder = FullFolder(_outputFolder());

        // A length loaded from library.json belongs to the file's current modified time (Load checked it).
        foreach (var item in _items.Values.Where(i => i.LengthSeconds is not null))
        {
            try
            {
                _modified[item.Path] = File.GetLastWriteTimeUtc(Path.Combine(_folder, item.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _failed(e);
            }
        }

        CreateMissingFolder();
        StartWatcher();
        Rescan();
        foreach (var item in _items.Values.Where(i => !i.BeingWritten && i.LengthSeconds is null).ToList())
        {
            QueueLength(item.Path);
        }

        _dirty |= _cleanupBlocked;
    }

    private void OnBegin(Begin begin)
    {
        var path = Path.GetFullPath(begin.Path);

        // A path outside the Output Folder isn't the Library's.
        if (!TryKey(_folder, path, out var key))
        {
            return;
        }

        _beingWritten[path] = key;

        // A write over an indexed file: a length read still queued for the old file is the old file's.
        _modified.Remove(key);
        _queued.Remove(key);
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
        item = item with
        {
            Size = file.Exists ? file.Length : 0,
            Modified = file.Exists ? Utc(file.LastWriteTimeUtc) : null,
            BeingWritten = false,
        };
        _items[key] = item;
        _changed(item);
        _dirty = true;
        QueueLength(key);
    }

    private void OnWatched(Watched watched)
    {
        if (watched.OldPath is { } old)
        {
            OnRenamed(old, watched.Path);
            return;
        }

        var path = watched.Path;
        if (!TryKey(_folder, path, out var key) || Ignored(path))
        {
            return;
        }

        // A folder moved or copied in reports only itself; a folder deleted reports only itself.
        if ((watched.Kind == WatcherChangeTypes.Created && Directory.Exists(path))
            || (watched.Kind == WatcherChangeTypes.Deleted && !IsMp4(path) && HasKeysUnder(key)))
        {
            PendRescan();
            return;
        }

        Pend(path);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        // The writer's .rbpart renamed over its reservation, or the echo of the actor's own move.
        if (Ignored(oldPath) || Ignored(newPath))
        {
            return;
        }

        var oldIn = TryKey(_folder, oldPath, out var oldKey);
        var newIn = TryKey(_folder, newPath, out var newKey);
        if (newIn && Directory.Exists(newPath))
        {
            if (oldIn)
            {
                MoveKeysUnder(oldKey, newKey);
            }

            PendRescan();
            return;
        }

        if (oldIn)
        {
            if (newIn && IsMp4(newPath) && _items.TryGetValue(oldKey, out var item) && !item.BeingWritten)
            {
                MoveItem(item, newKey);
            }
            else if (!IsMp4(oldPath) && HasKeysUnder(oldKey))
            {
                PendRescan();
            }
            else
            {
                Pend(oldPath);
            }
        }

        if (newIn)
        {
            Pend(newPath);
        }
    }

    private void OnSettle()
    {
        _settleArmed = false;
        if (_rescanPending)
        {
            _rescanPending = false;
            _pending.Clear();
            Rescan();
            return;
        }

        var paths = _pending.ToList();
        _pending.Clear();
        foreach (var path in paths)
        {
            Reconcile(path);
        }
    }

    private void OnWatchError(WatchError error)
    {
        _failed(error.Error);
        CreateMissingFolder();
        StartWatcher();
        _rescanPending = false;
        _pending.Clear();
        Rescan();
    }

    private void OnExpired(Expired expired)
    {
        if (_registered.TryGetValue(expired.Path, out var registration) && registration.Id == expired.Id)
        {
            _registered.Remove(expired.Path);
            registration.Timer.Dispose();
            Reconcile(expired.Path);
        }
    }

    private void OnFolderChanged()
    {
        var folder = FullFolder(_outputFolder());
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folder = folder;
        _items.Clear();
        _beingWritten.Clear();
        _pending.Clear();
        _modified.Clear();
        _queued.Clear();
        _rescanPending = false;
        foreach (var registration in _registered.Values)
        {
            registration.Timer.Dispose();
        }

        _registered.Clear();
        CreateMissingFolder();

        // Watched before the scan, so a file created meanwhile is reconciled once it settles.
        StartWatcher();
        foreach (var path in Files(folder, Extension, _failed))
        {
            if (TryKey(folder, path, out var key) && Check(path) is { } file)
            {
                _items[key] = Scanned(file, key, _profileForFolder);
            }
        }

        _folderChanged?.Invoke(new LibraryFolderChanged(folder));
        _reset(new LibraryReset([.. _items.Values]));
        _dirty = true;
        foreach (var key in _items.Keys.ToList())
        {
            QueueLength(key);
        }
    }

    private void OnAcknowledge()
    {
        if (_cleanupBlocked)
        {
            _cleanupBlocked = false;
            _cleanupBlockedChanged(new LibraryCleanupBlocked(false));
            _dirty = true;
        }
    }

    private void OnMove(MoveFile move)
    {
        var from = Path.GetFullPath(move.From);
        var to = Path.GetFullPath(move.To);
        if (!TryKey(_folder, from, out var fromKey) || !TryKey(_folder, to, out var toKey))
        {
            _failed(new ArgumentException($"A Library move must stay under the Output Folder: '{move.From}' to '{move.To}'."));
            return;
        }

        Register(from);
        Register(to);
        try
        {
            File.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _failed(e);
            return;
        }

        if (_items.TryGetValue(fromKey, out var item))
        {
            MoveItem(item, toKey);
        }
    }

    private void SetWatching(bool on)
    {
        _watching = on;
        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = on;
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                _failed(e);
            }
        }
    }

    /// <summary>
    /// Reconciles one path against disk (see the story's Design Notes): a file that is gone, isn't an
    /// <c>.mp4</c> or is empty is removed; one indexed at the same size is left alone; any other is
    /// listed only when it is a complete MP4. A busy file, or an incomplete one written in the last
    /// 2 s, is checked again once the next settle is due.
    /// </summary>
    private void Reconcile(string path)
    {
        if (!TryKey(_folder, path, out var key) || Ignored(path))
        {
            return;
        }

        var file = new FileInfo(path);
        if (!file.Exists || !IsMp4(path) || file.Length == 0)
        {
            Remove(key);
            return;
        }

        if (_items.TryGetValue(key, out var item) && item.Size == file.Length)
        {
            return;
        }

        if (Check(path) is null)
        {
            // A path Check pended for another look keeps its item, and its profile and date, until then.
            if (!_pending.Contains(path))
            {
                Remove(key);
            }

            return;
        }

        if (item is null)
        {
            item = Scanned(file, key, _profileForFolder);
            _items[key] = item;
            _added(item);
        }
        else
        {
            // A file rewritten outside the app has a new length.
            item = item with { Size = file.Length, Modified = Utc(file.LastWriteTimeUtc), LengthSeconds = null };
            _modified.Remove(key);
            _items[key] = item;
            _changed(item);
        }

        _dirty = true;
        QueueLength(key);
    }

    /// <summary>
    /// Sets the Favourite when the command's identity matches the item and, for a written item, the file
    /// on disk; else refuses it (see <see cref="TryMatch"/>).
    /// </summary>
    private void OnSetFavourite(SetFavouriteRequest request)
    {
        var args = request.Args;
        if (!TryMatch(args.Path, args.Size, args.Modified, request.Done, out var key, out var item))
        {
            return;
        }

        if (item.Favourite != args.Favourite)
        {
            item = item with { Favourite = args.Favourite };
            _items[key] = item;
            _changed(item);
            _dirty = true;
        }

        request.Done.TrySetResult();
    }

    /// <summary>
    /// Whether a command's identity (AD-3) matches the item and, unless it is being written, the file on
    /// disk; else it is refused with <see cref="ErrorCodes.LibraryItemChanged"/>. A file missing on disk is
    /// reconciled once it settles; one whose size or modified time differs is refreshed and published, so
    /// the App's next command matches.
    /// </summary>
    private bool TryMatch(string? path, long size, DateTimeOffset? modified, TaskCompletionSource done, out string key, out LibraryItem item)
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
        if (item.BeingWritten)
        {
            return true;
        }

        var full = Path.Combine(_folder, key);
        var file = new FileInfo(full);
        if (!file.Exists)
        {
            Pend(full);
            Refuse(done, path);
            return false;
        }

        var onDisk = Utc(file.LastWriteTimeUtc);
        if (file.Length != item.Size || onDisk != item.Modified)
        {
            var resized = file.Length != item.Size;
            item = item with { Size = file.Length, Modified = onDisk, LengthSeconds = resized ? null : item.LengthSeconds };
            if (resized)
            {
                _modified.Remove(key);
            }

            _items[key] = item;
            _changed(item);
            _dirty = true;
            if (resized)
            {
                QueueLength(key);
            }

            Refuse(done, path);
            return false;
        }

        return true;
    }

    /// <summary>The indexed item a command's relative path names.</summary>
    private bool TryItem(string? path, out string key, [NotNullWhen(true)] out LibraryItem? item)
    {
        item = null;
        key = "";
        return !string.IsNullOrEmpty(path)
            && TryKey(_folder, Path.GetFullPath(Path.Combine(_folder, path)), out key)
            && _items.TryGetValue(key, out item);
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

    /// <summary>
    /// Renames a written item's file in its folder (see <see cref="Rename"/>): both paths are registered
    /// first, so the watcher's echo is ignored, and the move keeps the item's metadata.
    /// </summary>
    private void OnRename(RenameRequest request)
    {
        var args = request.Args;
        if (RefuseBeingWritten(args.Path, request.Done)
            || !TryMatch(args.Path, args.Size, args.Modified, request.Done, out var key, out var item))
        {
            return;
        }

        if (!ValidName(args.NewName))
        {
            request.Done.TrySetException(new ErrorCodeException(
                ErrorCodes.InvalidFileName,
                $"'{args.NewName}' can't be used as a file name."));
            return;
        }

        var name = args.NewName + Extension;
        if (string.Equals(Path.GetFileName(key), name, StringComparison.Ordinal))
        {
            request.Done.TrySetResult();
            return;
        }

        var newKey = Path.Combine(Path.GetDirectoryName(key) ?? "", name);
        var from = Path.Combine(_folder, key);
        var to = Path.Combine(_folder, newKey);

        // A case-only rename of the same file is allowed; any other file, item or write there isn't.
        var sameFile = string.Equals(newKey, key, StringComparison.OrdinalIgnoreCase);
        if (!sameFile && (_items.ContainsKey(newKey) || _beingWritten.ContainsKey(to) || File.Exists(to) || Directory.Exists(to)))
        {
            request.Done.TrySetException(new ErrorCodeException(
                ErrorCodes.FileNameTaken,
                $"A file named '{name}' already exists there."));
            return;
        }

        Register(from);
        Register(to);
        if (!Retried(() => File.Move(from, to), request.Done, "The file couldn't be renamed."))
        {
            return;
        }

        MoveItem(item, newKey);
        request.Done.TrySetResult();
    }

    /// <summary>
    /// Moves a written item's file to the Recycle Bin (see <see cref="Delete"/>); its path is registered
    /// first, so the watcher's echo is ignored.
    /// </summary>
    private void OnDelete(DeleteRequest request)
    {
        var args = request.Args;
        if (RefuseBeingWritten(args.Path, request.Done)
            || !TryMatch(args.Path, args.Size, args.Modified, request.Done, out var key, out _))
        {
            return;
        }

        var path = Path.Combine(_folder, key);
        var recycled = false;
        if (_recycle is { } recycle)
        {
            Register(path);
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

        Remove(key);
        request.Done.TrySetResult();
    }

    /// <summary>
    /// Runs a file operation, trying a sharing violation again up to 10 times 50 ms apart; any other
    /// failure, or the last try, is reported and refuses the command with <see cref="ErrorCodes.CommandFailed"/>.
    /// </summary>
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

    /// <summary>Queues a read of the item's length at its current size, unless one is already queued at that size.</summary>
    private void QueueLength(string key)
    {
        if (_readLength is null
            || !_items.TryGetValue(key, out var item)
            || item.BeingWritten
            || (_queued.TryGetValue(key, out var size) && size == item.Size))
        {
            return;
        }

        if (_lengthJobs.Writer.TryWrite(new LengthJob(_folder, key, Path.Combine(_folder, key), item.Size)))
        {
            _queued[key] = item.Size;
        }
    }

    /// <summary>
    /// The worker read a length: applied only when the folder is the same, and the item still exists,
    /// isn't being written and has the size the job was queued at.
    /// </summary>
    private void OnLengthRead(LengthRead read)
    {
        if (!string.Equals(read.Folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_queued.TryGetValue(read.Key, out var queued) && queued == read.Size)
        {
            _queued.Remove(read.Key);
        }

        if (read.Length is not { } length
            || !_items.TryGetValue(read.Key, out var item)
            || item.BeingWritten
            || item.Size != read.Size)
        {
            return;
        }

        item = item with { LengthSeconds = length };
        _items[read.Key] = item;
        _modified[read.Key] = read.Modified;
        _changed(item);
        _dirty = true;
    }

    /// <summary>The file when it is a non-empty, complete MP4; else null, pending a busy or recently written file for another check.</summary>
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
                Pend(path);
            }
        }
        catch (IOException)
        {
            // Locked, e.g. a copy still running.
            Pend(path);
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
        foreach (var key in _items.Keys)
        {
            paths.Add(Path.Combine(_folder, key));
        }

        paths.UnionWith(Files(_folder, Extension, _failed));
        foreach (var path in paths)
        {
            Reconcile(path);
        }
    }

    /// <summary>A write under way, its <c>.rbpart</c>, or a path of the actor's own operation not yet expired.</summary>
    private bool Ignored(string path) =>
        _beingWritten.ContainsKey(path)
        || (path.EndsWith(PartExtension, StringComparison.OrdinalIgnoreCase) && _beingWritten.ContainsKey(path[..^PartExtension.Length]))
        || _registered.ContainsKey(path);

    private bool HasKeysUnder(string prefix)
    {
        var folder = prefix + Path.DirectorySeparatorChar;
        return _items.Keys.Any(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    // A folder renamed: each item under it moves, keeping its profile and date.
    private void MoveKeysUnder(string oldPrefix, string newPrefix)
    {
        var folder = oldPrefix + Path.DirectorySeparatorChar;
        foreach (var item in _items.Values.Where(i => !i.BeingWritten && i.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            MoveItem(item, newPrefix + item.Path[oldPrefix.Length..]);
        }
    }

    private void MoveItem(LibraryItem item, string newKey)
    {
        _items.Remove(item.Path);
        _removed(new LibraryItemRemoved(item.Path));
        var moved = item with { Path = newKey };
        _items[newKey] = moved;
        _added(moved);
        _dirty = true;

        // A move keeps the length; one not yet read is read under the new key.
        _queued.Remove(item.Path);
        if (_modified.Remove(item.Path, out var modified))
        {
            _modified[newKey] = modified;
        }

        if (moved.LengthSeconds is null)
        {
            QueueLength(newKey);
        }
    }

    private void Remove(string key)
    {
        _modified.Remove(key);
        _queued.Remove(key);
        if (_items.Remove(key))
        {
            _removed(new LibraryItemRemoved(key));
            _dirty = true;
        }
    }

    private void Pend(string path)
    {
        _pending.Add(path);
        ArmSettle();
    }

    private void PendRescan()
    {
        _rescanPending = true;
        ArmSettle();
    }

    // Not re-armed by later events, so a path that keeps changing is still checked every 250 ms.
    private void ArmSettle()
    {
        if (!_settleArmed && _settle is not null)
        {
            _settleArmed = true;
            _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Register(string path)
    {
        if (_registered.Remove(path, out var previous))
        {
            previous.Timer.Dispose();
        }

        var id = ++_registrations;
        var timer = _time.CreateTimer(_ => _channel.Writer.TryWrite(new Expired(path, id)), null, RegistrationTime, Timeout.InfiniteTimeSpan);
        _registered[path] = new Registration(id, timer);
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

    // Callbacks only post (AD-17).
    private void StartWatcher()
    {
        StopWatcher();
        if (!Directory.Exists(_folder))
        {
            return;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(_folder, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = WatcherBufferSize,
            };
            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = _watching;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            _failed(e);
        }
    }

    private void StopWatcher()
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

    private void OnFileEvent(object sender, FileSystemEventArgs e) => _channel.Writer.TryWrite(new Watched(e.ChangeType, e.FullPath, null));

    private void OnFileRenamed(object sender, RenamedEventArgs e) => _channel.Writer.TryWrite(new Watched(e.ChangeType, e.FullPath, e.OldFullPath));

    private void OnError(object sender, ErrorEventArgs e) => OnWatcherError(e.GetException());

    // Atomic: a temp file flushed to disk, then swapped in, keeping the previous file as library.json.bak.
    private void Save()
    {
        var temp = _libraryFile + ".tmp";
        try
        {
            var document = new LibraryFile(
                1,
                [.. _items.Values.Where(i => !i.BeingWritten).Select(Entry)],
                _folder,
                _cleanupBlocked);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_libraryFile))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    JsonSerializer.Serialize(writer, document, LibraryFileJsonContext.Default.LibraryFile);
                }

                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_libraryFile))
            {
                File.Replace(temp, _libraryFile, _libraryFile + BakExtension, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _libraryFile);
            }
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

    // A length is saved with the modified time it was read at, so a restart can tell it still holds.
    private LibraryFileEntry Entry(LibraryItem item) =>
        item.LengthSeconds is { } length && _modified.TryGetValue(item.Path, out var modified)
            ? new LibraryFileEntry(item.Path, item.ProfileId, item.Date, item.Size, length, modified, item.Favourite)
            : new LibraryFileEntry(item.Path, item.ProfileId, item.Date, item.Size, Favourite: item.Favourite);

    private enum ReadResult
    {
        Missing,
        Read,
        Bad,
    }

    private sealed record Registration(long Id, ITimer Timer);

    private abstract record Message;

    private sealed record Start : Message;

    private sealed record Begin(string Path, string? ProfileId) : Message;

    private sealed record End(string Path, WriteOutcome Outcome) : Message;

    private sealed record Watched(WatcherChangeTypes Kind, string Path, string? OldPath) : Message;

    private sealed record Settle : Message;

    private sealed record WatchError(Exception Error) : Message;

    private sealed record Expired(string Path, long Id) : Message;

    private sealed record FolderChanged : Message;

    private sealed record Acknowledge : Message;

    private sealed record MoveFile(string From, string To) : Message;

    private sealed record Watch(bool On, TaskCompletionSource Done) : Message;

    private sealed record LengthRead(string Folder, string Key, long Size, DateTime Modified, double? Length) : Message;

    private sealed record SetFavouriteRequest(LibrarySetFavourite Args, TaskCompletionSource Done) : Message;

    private sealed record RenameRequest(LibraryRename Args, TaskCompletionSource Done) : Message;

    private sealed record DeleteRequest(LibraryDelete Args, TaskCompletionSource Done) : Message;

    private sealed record LengthJob(string Folder, string Key, string Path, long Size);
}

/// <summary>
/// <c>library.json</c>: the saved items, the Output Folder they were saved for (null in a file from
/// before story 7.2, which is read for any folder), and whether a rebuilt index is unacknowledged.
/// </summary>
internal sealed record LibraryFile(int Version, IReadOnlyList<LibraryFileEntry> Items, string? OutputFolder = null, bool CleanupBlocked = false);

/// <summary>
/// One saved item: its path relative to the Output Folder, its Game Profile, its date, and its size
/// when saved (null in a file from before story 7.2), which tells a file changed outside the app. Its
/// length in seconds and the file's modified time (UTC) it was read at, both null when no length was
/// read (or in a file from before story 7.3); the length is reused only while size and time match.
/// Whether it is a Favourite (story 7.4); kept through a size change while the engine was off.
/// </summary>
internal sealed record LibraryFileEntry(string Path, string? ProfileId, DateTimeOffset Date, long? Size = null, double? LengthSeconds = null, DateTime? Modified = null, bool Favourite = false);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LibraryFile))]
internal sealed partial class LibraryFileJsonContext : JsonSerializerContext;
