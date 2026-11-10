using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Output;
using Encore.Contracts.Settings;

namespace Encore.Core.Library;

/// <summary>The single-threaded Library index: every change, disk check and save runs on its own thread. docs/code-notes.md.</summary>
public sealed partial class LibraryActor : ILibraryWrites, IDisposable
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

    /// <summary>How soon a failed save of the index is tried again.</summary>
    internal static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(1);

    // An incomplete file written this recently may still be growing, so it is checked again.
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);

    // How long the watcher's echo of the actor's own file operation is ignored.
    private static readonly TimeSpan RegistrationTime = TimeSpan.FromSeconds(5);

    // A rename or recycle that meets a sharing violation (e.g. the length worker's read) is tried again.
    private static readonly TimeSpan SharingRetryDelay = TimeSpan.FromMilliseconds(50);

    // Cleanup never runs this soon after start, and runs at least this often.
    private static readonly TimeSpan CleanupStartDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);

    private static readonly CleanupSettings CleanupOff = new(null, null);

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
        // Hidden and system files are listed; a junction or symbolic link is neither followed nor listed.
        AttributesToSkip = FileAttributes.ReparsePoint,
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
    private readonly Func<string, string>? _finalPath;
    private readonly Func<CleanupSettings>? _cleanup;
    private readonly Action<string>? _limitUnreachable;
    private readonly Action<string>? _log;
    private readonly Thread _thread;

    // The length worker's jobs; written by the actor thread only.
    private readonly Channel<LengthJob> _lengthJobs = Channel.CreateUnbounded<LengthJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });

    private readonly Thread? _lengthThread;

    // Actor thread only.
    private readonly Dictionary<string, LibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: each write under way, by absolute path, to its item's key.
    private readonly Dictionary<string, string> _beingWritten = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the paths of the actor's own file operations, by absolute path, to their expiry.
    private readonly Dictionary<string, Registration> _registered = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the absolute paths watcher events named, reconciled once they settle.
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the modified time (LastWriteTimeUtc) each item's length was read at, by key.
    private readonly Dictionary<string, DateTime> _modified = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the size each queued length job was queued at, by key, so a job isn't queued twice.
    private readonly Dictionary<string, long> _queued = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the keys the running Cleanup pass couldn't delete; cleared when a pass starts.
    private readonly HashSet<string> _cleanupSkip = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: whether the running pass found each key's file inside the Output Folder; the delete checks again.
    private readonly Dictionary<string, bool> _cleanupInside = new(StringComparer.OrdinalIgnoreCase);

    private string _folder = "";
    private FileSystemWatcher? _watcher;
    private ITimer? _settle;
    private ITimer? _saveRetry;
    private bool _saveFailing;
    private bool _settleArmed;
    private bool _rescanPending;
    private bool _cleanupBlocked;
    private bool _dirty;
    private bool _started;
    private DateTimeOffset _startedAt;
    private CleanupSettings _lastCleanup = CleanupOff;
    private ITimer? _cleanupFirst;
    private ITimer? _cleanupPeriodic;
    private bool _passRunning;
    private int _passDeleted;
    private long _passBytes;
    private int _passSkipped;
    private bool _unreachableRaised;
    private long _registrations;
    private volatile bool _watching = true;
    private int _disposed;

    /// <summary>The publish callbacks run on the actor thread; each optional port is explained in docs/code-notes.md.</summary>
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
        Func<string, bool>? recycle = null,
        Func<CleanupSettings>? cleanup = null,
        Action<string>? limitUnreachable = null,
        Action<string>? log = null,
        Func<string, string>? finalPath = null)
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
        _cleanup = cleanup;
        _limitUnreachable = limitUnreachable;
        _log = log;
        _finalPath = finalPath;
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

    /// <summary>A test seam: whether the watcher raises events; setting it waits until the actor has applied it.</summary>
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

    /// <summary>The Library at start: clears a crash's leftovers, reads the index or its backup, then lists the files on disk.</summary>
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

                // A file the index doesn't know at this size must be a complete MP4; an old entry has no size.
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
                    ? new LibraryItem(key, entry.ProfileId, file.Length, entry.Date, false, length, entry.Favourite, Utc(file.LastWriteTimeUtc), entry.Kind ?? ScannedKind(key))
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
    public void BeginWrite(string path, string? profileId) => BeginWrite(path, profileId, null);

    /// <inheritdoc/>
    public void BeginWrite(string path, string? profileId, string? kind)
    {
        if (path is not null)
        {
            _channel.Writer.TryWrite(new Begin(path, profileId, kind));
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

    /// <c>library.setFavourite</c>: sets the Favourite when the identity matches; faults when it is stale.
    public Task SetFavourite(LibrarySetFavourite args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post(done => new SetFavouriteRequest(args, done));
    }

    /// <c>library.rename</c>: renames the item's file in its folder, keeping extension and metadata; faults when refused.
    public Task Rename(LibraryRename args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post(done => new RenameRequest(args, done));
    }

    /// <c>library.delete</c>: moves the item's file to the Recycle Bin and removes the item; faults when refused.
    public Task Delete(LibraryDelete args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post(done => new DeleteRequest(args, done));
    }

    /// <summary>The settings changed: when the Output Folder is a different folder, the index is rebuilt from it and published.</summary>
    public void OutputFolderChanged() => _channel.Writer.TryWrite(new FolderChanged());

    /// <summary>The <c>library-corrupt</c> warning was dismissed: Cleanup is no longer blocked.</summary>
    public void AcknowledgeCorrupt() => _channel.Writer.TryWrite(new Acknowledge());

    /// <summary>The settings changed: when the <c>cleanup</c> limits differ from the last seen, a Cleanup pass may start.</summary>
    public void CleanupChanged() => _channel.Writer.TryWrite(new CleanupTick(Changed: true));

    /// <c>cleanup.preview</c>: what a pass with these limits would delete now, may it run or not; nothing is deleted.
    public Task<LibraryCleanupPlan> PreviewCleanup(LibraryCleanupPreview args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Post<LibraryCleanupPlan>(done => new PreviewRequest(args, done));
    }

    /// <summary>The watcher's <see cref="FileSystemWatcher.Error"/> handler: a buffer overflow, or the folder went away.</summary>
    internal void OnWatcherError(Exception error) => _channel.Writer.TryWrite(new WatchError(error));

    /// <summary>Stops taking messages, handles those already posted and joins the thread; then stops the length worker.</summary>
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

    /// <summary>Whether a name can be a file's base name: not empty, no forbidden character, no trailing dot, not reserved.</summary>
    private static bool ValidName(string? name)
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

    internal static bool IsMp4(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the path, or a folder above it up to the given folder, is a junction or link; true when unreadable.</summary>
    internal static bool ThroughReparsePoint(string folder, string path)
    {
        try
        {
            // The Output Folder itself may be a junction; the file and each folder below it may not.
            for (var at = path; at is not null && at.Length > folder.Length; at = Path.GetDirectoryName(at))
            {
                if (File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Can't tell, so Cleanup doesn't touch it.
            return true;
        }
    }

    /// <summary>The key of a path under a folder; false for a path outside it, or the folder itself.</summary>
    internal static bool TryKey(string folder, string path, out string key)
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
        return new LibraryItem(key, profileId, file.Length, new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero), false, Modified: Utc(file.LastWriteTimeUtc), Kind: ScannedKind(key));
    }

    /// <summary>A scanned file's kind: a Monitor Recording when it is in that folder; otherwise unknown.</summary>
    private static string? ScannedKind(string key) =>
        key.StartsWith(ProfileFolders.MonitorRecordings + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? LibraryItemKinds.MonitorRecording
            : null;

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

    /// <summary>The files under a folder with an extension; a failed enumeration is reported, keeping what it found.</summary>
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

    /// <summary>The saved entries from <c>library.json</c>, else its backup; corrupt when neither parses and one exists.</summary>
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
            done.TrySetException(ShutDown());
        }

        return done.Task;
    }

    // A query's request, answered by the actor with its result; faulted at once once the actor is disposed.
    private Task<T> Post<T>(Func<TaskCompletionSource<T>, Message> request)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(request(done)))
        {
            done.TrySetException(ShutDown());
        }

        return done.Task;
    }

    private static ErrorCodeException ShutDown() => new(ErrorCodes.EngineDisconnected, "The Library has shut down.");

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
        _saveRetry?.Dispose();
        _cleanupFirst?.Dispose();
        _cleanupPeriodic?.Dispose();
        DisposeRegistrations();
    }

    // The length worker: it only reads a file and posts the result to the actor.
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

            case RetrySave:
                OnRetrySave();
                break;

            case Watch watch:
                OnWatch(watch);
                break;

            case LengthRead read:
                OnLengthRead(read);
                break;

            case SetFavouriteRequest request:
                Answered(request.Done, "The Favourite couldn't be set.", () => OnSetFavourite(request));
                break;

            case RenameRequest request:
                Answered(request.Done, "The file couldn't be renamed.", () => OnRename(request));
                break;

            case DeleteRequest request:
                Answered(request.Done, "The file couldn't be deleted.", () => OnDelete(request));
                break;

            case CleanupTick tick:
                OnCleanupTick(tick);
                break;

            case CleanupStep:
                OnCleanupStepOrEndPass();
                break;

            case PreviewRequest request:
                OnPreview(request);
                break;
        }
    }

    // Runs a command's handler; an unexpected failure (reported by Run) mustn't leave the router waiting for its answer.
    private static void Answered(TaskCompletionSource done, string unanswered, Action handler)
    {
        try
        {
            handler();
        }
        finally
        {
            // A no-op once the handler has answered.
            done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, unanswered));
        }
    }

    // The next loop of Run saves again.
    private void OnRetrySave() => _dirty = true;

    private void OnWatch(Watch watch)
    {
        SetWatching(watch.On);
        watch.Done.TrySetResult();
    }

    private void OnCleanupStepOrEndPass()
    {
        try
        {
            OnCleanupStep();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A step that fails ends its pass, so the next trigger starts another.
            _passRunning = false;
            _failed(e);
        }
    }

    private void OnPreview(PreviewRequest request)
    {
        try
        {
            request.Done.TrySetResult(Preview(request.Args));
        }
        finally
        {
            // A no-op once answered.
            request.Done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, "The Cleanup preview failed."));
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

        // Cleanup waits 60 s after start, then runs at least every 10 minutes.
        _started = true;
        _startedAt = _time.GetUtcNow();
        _lastCleanup = CleanupLimits();
        _cleanupFirst = _time.CreateTimer(OnCleanupTimer, null, CleanupStartDelay, Timeout.InfiniteTimeSpan);
        _cleanupPeriodic = _time.CreateTimer(OnCleanupTimer, null, CleanupInterval, CleanupInterval);
    }

    // Both Cleanup timers only post.
    private void OnCleanupTimer(object? state) => _channel.Writer.TryWrite(new CleanupTick(Changed: false));

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
        var item = new LibraryItem(key, begin.ProfileId, 0, _time.GetUtcNow(), true, Kind: begin.Kind);
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
        StartCleanup();
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
            RescanNow();
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
        RescanNow();
    }

    // A pending rescan, or a watcher restart, rescans the whole folder; its end may start a Cleanup pass.
    private void RescanNow()
    {
        _rescanPending = false;
        _pending.Clear();
        Rescan();
        StartCleanup();
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
        _cleanupSkip.Clear();
        _rescanPending = false;
        DisposeRegistrations();
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

        StartCleanup();
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

    /// <summary>Checks one path against disk: removes, keeps or lists it; a busy or incomplete file is checked again later.</summary>
    private void Reconcile(string path)
    {
        if (!TryKey(_folder, path, out var key) || Ignored(path))
        {
            return;
        }

        var file = new FileInfo(path);
        if (!file.Exists || !IsMp4(path) || file.Length == 0 || ThroughReparsePoint(_folder, path))
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
            item = item with { Favourite = args.Favourite };
            _items[key] = item;
            _changed(item);
            _dirty = true;
        }

        request.Done.TrySetResult();
    }

    /// <summary>Whether a command's identity matches the item and the file on disk; a stale one is refused and refreshed.</summary>
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

    /// <summary>Renames a written item's file; both paths are registered first, so the watcher's echo is ignored.</summary>
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

        var name = args.NewName + Path.GetExtension(key);
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

        if (!Inside(Path.GetFullPath(from)))
        {
            RefuseOutside(request.Done, args.Path);
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

    /// <summary>The <c>cleanup</c> limits now; off without the port.</summary>
    private CleanupSettings CleanupLimits() => _cleanup?.Invoke() ?? CleanupOff;

    /// <summary>Whether Cleanup may run now: a limit is set, no rescan is pending, the index is trusted, and 60 s have passed.</summary>
    private bool CleanupOpen(CleanupSettings limits) =>
        (limits.MaxBytes is not null || limits.MaxAgeSeconds is not null)
        && _started
        && !_rescanPending
        && !_cleanupBlocked
        && _time.GetUtcNow() - _startedAt >= CleanupStartDelay;

    private CleanupPlan PlanCleanup(CleanupSettings limits, IReadOnlySet<string> skip, Dictionary<string, bool>? cache) =>
        CleanupPlanner.Plan(_time.GetUtcNow(), limits, _items.Values, _folder, skip, key =>
        {
            if (cache is null || !cache.TryGetValue(key, out var inside))
            {
                inside = Inside(Path.GetFullPath(Path.Combine(_folder, key)));
                cache?.Add(key, inside);
            }

            return inside;
        });

    private LibraryCleanupPlan Preview(LibraryCleanupPreview args)
    {
        var plan = PlanCleanup(new CleanupSettings(args.MaxBytes, args.MaxAgeSeconds), new HashSet<string>(), null);
        return new LibraryCleanupPlan(plan.Items.Count, plan.Bytes, [.. plan.Items.Select(i => i.Path)]);
    }

    private void OnCleanupTick(CleanupTick tick)
    {
        if (tick.Changed)
        {
            var limits = CleanupLimits();
            if (limits == _lastCleanup)
            {
                return;
            }

            _lastCleanup = limits;
        }

        StartCleanup();
    }

    /// <summary>Starts a Cleanup pass unless one runs or Cleanup may not run: logs its plan, then posts its first step.</summary>
    private void StartCleanup()
    {
        if (_passRunning)
        {
            return;
        }

        var limits = CleanupLimits();
        if (!CleanupOpen(limits))
        {
            RearmWhenOff(limits);
            return;
        }

        _cleanupSkip.Clear();
        _cleanupInside.Clear();
        _passDeleted = 0;
        _passBytes = 0;
        _passSkipped = 0;
        var plan = PlanCleanup(limits, _cleanupSkip, _cleanupInside);
        if (plan.Items.Count == 0)
        {
            EndCleanup(plan);
            return;
        }

        _passRunning = true;

        // Logged before the first step is posted, so what is posted meanwhile is handled before it.
        Log($"Cleanup plan: {plan.Items.Count} files, {plan.Bytes} bytes: {string.Join(", ", plan.Items.Select(i => $"{i.Path} ({i.Size} bytes)"))}.");
        _channel.Writer.TryWrite(new CleanupStep());
    }

    /// <summary>One step of a pass: plans again, then deletes the plan's first item once it is re-checked, or ends the pass.</summary>
    private void OnCleanupStep()
    {
        if (!_passRunning)
        {
            return;
        }

        var limits = CleanupLimits();
        if (!CleanupOpen(limits))
        {
            _passRunning = false;
            RearmWhenOff(limits);
            Log($"Cleanup stopped after deleting {_passDeleted} files ({_passBytes} bytes): it may not run now.");
            return;
        }

        var plan = PlanCleanup(limits, _cleanupSkip, _cleanupInside);
        if (plan.Items.Count == 0)
        {
            _passRunning = false;
            EndCleanup(plan);
            return;
        }

        var item = plan.Items[0];
        if (CleanupDelete(item.Path))
        {
            _passDeleted++;
            _passBytes += item.Size;
        }
        else
        {
            // Not tried again in this pass.
            _cleanupSkip.Add(item.Path);
            _passSkipped++;
        }

        _channel.Writer.TryWrite(new CleanupStep());
    }

    /// <summary>A pass ended with nothing left to delete: raises <c>cleanup-limit-unreachable</c> once, or re-arms it.</summary>
    private void EndCleanup(CleanupPlan plan)
    {
        // Re-armed only once the folder is back within the size limit, not by a pass that merely skipped a file.
        if (CleanupLimits().MaxBytes is not { } maxBytes || plan.Remaining <= maxBytes)
        {
            _unreachableRaised = false;
        }
        else if (plan.Unreachable && !_unreachableRaised)
        {
            _unreachableRaised = true;
            _limitUnreachable?.Invoke(
                $"Cleanup can't keep the Clips folder within its size limit of {maxBytes / 1e9:0.#} GB: the {plan.Remaining / 1e9:0.#} GB left are Favourites or files being saved.");
        }

        Log($"Cleanup finished: {_passDeleted} files deleted ({_passBytes} bytes), {_passSkipped} skipped; the Library holds {plan.Remaining} bytes.");
    }

    // Turning Cleanup off re-arms cleanup-limit-unreachable.
    private void RearmWhenOff(CleanupSettings limits)
    {
        if (limits.MaxBytes is null && limits.MaxAgeSeconds is null)
        {
            _unreachableRaised = false;
        }
    }

    /// <summary>Deletes an item's file for good once it is re-checked against the index and the disk; false when it wasn't.</summary>
    private bool CleanupDelete(string key)
    {
        if (!_items.TryGetValue(key, out var item) || item.BeingWritten || item.Favourite)
        {
            Log($"Cleanup skipped {key}: it is gone, being written or a Favourite.");
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(_folder, key));
        if (_beingWritten.ContainsKey(full) || !TryKey(_folder, full, out _) || !IsMp4(full))
        {
            Log($"Cleanup skipped {key}: it is being written, outside the Output Folder or not an .mp4.");
            return false;
        }

        var file = new FileInfo(full);
        if (!file.Exists || file.Length != item.Size)
        {
            Pend(full);
            LogChangedOnDisk(key);
            return false;
        }

        if (!Inside(full))
        {
            Log($"Cleanup skipped {key}: it is reached through a junction or symbolic link, or its final path isn't in the Output Folder.");
            return false;
        }

        // Touched at the same size (reconcile compares sizes only): refreshed like a command's, for the next pass.
        var onDisk = Utc(file.LastWriteTimeUtc);
        if (onDisk != item.Modified)
        {
            _items[key] = item with { Modified = onDisk };
            _changed(_items[key]);
            _dirty = true;
            LogChangedOnDisk(key);
            return false;
        }

        Register(full);
        try
        {
            File.Delete(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Cleanup skipped {key}: {e.Message}");
            return false;
        }

        Remove(key, LibraryRemovalReasons.Cleanup);
        Log($"Cleanup deleted {key} ({item.Size} bytes).");
        return true;
    }

    private void Log(string message) => _log?.Invoke(message);

    private void LogChangedOnDisk(string key) => Log($"Cleanup skipped {key}: the file changed on disk.");

    /// <summary>Whether the actor may change the file: no junction or link on its path, and it is under the Output Folder.</summary>
    private bool Inside(string full)
    {
        if (!TryKey(_folder, full, out _) || ThroughReparsePoint(_folder, full))
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

    /// <summary>The worker read a length: applied only when the folder, the item and its size are still the same.</summary>
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

    private void Remove(string key, string? reason = null)
    {
        _modified.Remove(key);
        _queued.Remove(key);
        if (_items.Remove(key))
        {
            _removed(new LibraryItemRemoved(key, reason));
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

    // Disposes each registration's expiry timer; the caller clears the map when it is still used.
    private void DisposeRegistrations()
    {
        foreach (var registration in _registered.Values)
        {
            registration.Timer.Dispose();
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

    // Callbacks only post.
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

    // Atomic: a temp file is flushed, then swapped in, keeping a backup; a failed save is retried until it lands.
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

            if (_saveFailing)
            {
                _saveFailing = false;
                _log?.Invoke("library.json saved after an earlier save failed.");
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

            // One warning per run of failures; the retry keeps going quietly.
            if (!_saveFailing)
            {
                _failed(e);
            }

            _saveFailing = true;
            _saveRetry ??= _time.CreateTimer(_ => _channel.Writer.TryWrite(new RetrySave()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _saveRetry.Change(SaveRetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // A length is saved with the modified time it was read at, so a restart can tell it still holds.
    private LibraryFileEntry Entry(LibraryItem item) =>
        item.LengthSeconds is { } length && _modified.TryGetValue(item.Path, out var modified)
            ? new LibraryFileEntry(item.Path, item.ProfileId, item.Date, item.Size, length, modified, item.Favourite, item.Kind)
            : new LibraryFileEntry(item.Path, item.ProfileId, item.Date, item.Size, Favourite: item.Favourite, Kind: item.Kind);

    private enum ReadResult
    {
        Missing,
        Read,
        Bad,
    }

    private sealed record Registration(long Id, ITimer Timer);

    private sealed record LengthJob(string Folder, string Key, string Path, long Size);
}
