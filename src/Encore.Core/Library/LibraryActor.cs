using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Library;

/// <summary>The single-threaded Library index: every change, disk check and save runs on its own thread. docs/code-notes.md.</summary>
public sealed partial class LibraryActor : ILibraryWrites, IDisposable
{
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

    private readonly Channel<Message> _channel = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly Func<string> _outputFolder;
    private readonly string _libraryFile;
    private readonly Func<string, string?> _profileForFolder;
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

    // Actor thread only: the items, and what is known of their writes and lengths.
    private readonly LibraryIndex _index;

    // The length worker's jobs; written by the actor thread only.
    private readonly Channel<LengthJob> _lengthJobs = Channel.CreateUnbounded<LengthJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });

    private readonly Thread? _lengthThread;

    // Actor thread only: the paths of the actor's own file operations, by absolute path, to their expiry.
    private readonly Dictionary<string, Registration> _registered = new(StringComparer.OrdinalIgnoreCase);

    // Actor thread only: the absolute paths watcher events named, reconciled once they settle.
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

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
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(removed);
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
        _index = new LibraryIndex(items, added, changed, removed, () => _dirty = true);

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
        _folder = LibraryPaths.FullFolder(_outputFolder());

        // A length loaded from library.json belongs to the file's current modified time (Load checked it).
        foreach (var item in _index.Items.Where(i => i.LengthSeconds is not null))
        {
            try
            {
                _index.RememberLengthReadAt(item.Path, File.GetLastWriteTimeUtc(Path.Combine(_folder, item.Path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _failed(e);
            }
        }

        CreateMissingFolder();
        StartWatcher();
        Rescan();
        foreach (var item in _index.Items.Where(i => !i.BeingWritten && i.LengthSeconds is null).ToList())
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
        if (!LibraryPaths.TryKey(_folder, path, out var key))
        {
            return;
        }

        _index.BeginWrite(path, new LibraryItem(key, begin.ProfileId, 0, _time.GetUtcNow(), true, Kind: begin.Kind));
    }

    private void OnEnd(End end)
    {
        var path = Path.GetFullPath(end.Path);
        if (!_index.TryEndWrite(path, out var item))
        {
            return;
        }

        var key = item.Path;
        if (end.Outcome == WriteOutcome.Failed)
        {
            _index.DropFailedWrite(key);
            return;
        }

        var file = new FileInfo(path);
        item = item with
        {
            Size = file.Exists ? file.Length : 0,
            Modified = file.Exists ? LibraryFiles.Utc(file.LastWriteTimeUtc) : null,
            BeingWritten = false,
        };
        _index.Change(item);
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
        if (!LibraryPaths.TryKey(_folder, path, out var key) || Ignored(path))
        {
            return;
        }

        // A folder moved or copied in reports only itself; a folder deleted reports only itself.
        if ((watched.Kind == WatcherChangeTypes.Created && Directory.Exists(path))
            || (watched.Kind == WatcherChangeTypes.Deleted && !LibraryPaths.IsMp4(path) && _index.HasKeysUnder(key)))
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

        var oldIn = LibraryPaths.TryKey(_folder, oldPath, out var oldKey);
        var newIn = LibraryPaths.TryKey(_folder, newPath, out var newKey);
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
            if (newIn && LibraryPaths.IsMp4(newPath) && _index.TryGet(oldKey, out var item) && !item.BeingWritten)
            {
                MoveItem(item, newKey);
            }
            else if (!LibraryPaths.IsMp4(oldPath) && _index.HasKeysUnder(oldKey))
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
        var folder = LibraryPaths.FullFolder(_outputFolder());
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folder = folder;
        _index.Clear();
        _pending.Clear();
        _cleanupSkip.Clear();
        _rescanPending = false;
        DisposeRegistrations();
        _registered.Clear();
        CreateMissingFolder();

        // Watched before the scan, so a file created meanwhile is reconciled once it settles.
        StartWatcher();
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

        var onDisk = LibraryFiles.Utc(file.LastWriteTimeUtc);
        if (file.Length != item.Size || onDisk != item.Modified)
        {
            var resized = file.Length != item.Size;
            item = item with { Size = file.Length, Modified = onDisk, LengthSeconds = resized ? null : item.LengthSeconds };
            if (resized)
            {
                _index.ForgetLength(key);
            }

            _index.Change(item);
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
        CleanupPlanner.Plan(_time.GetUtcNow(), limits, _index.Items, _folder, skip, key =>
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
        if (!_index.TryGet(key, out var item) || item.BeingWritten || item.Favourite)
        {
            Log($"Cleanup skipped {key}: it is gone, being written or a Favourite.");
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(_folder, key));
        if (_index.IsBeingWritten(full) || !LibraryPaths.TryKey(_folder, full, out _) || !LibraryPaths.IsMp4(full))
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
        var onDisk = LibraryFiles.Utc(file.LastWriteTimeUtc);
        if (onDisk != item.Modified)
        {
            _index.Change(item with { Modified = onDisk });
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

        _index.Remove(key, LibraryRemovalReasons.Cleanup);
        Log($"Cleanup deleted {key} ({item.Size} bytes).");
        return true;
    }

    private void Log(string message) => _log?.Invoke(message);

    private void LogChangedOnDisk(string key) => Log($"Cleanup skipped {key}: the file changed on disk.");

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

    /// <summary>Queues a read of the item's length at its current size, unless one is already queued at that size.</summary>
    private void QueueLength(string key)
    {
        if (_readLength is null || !_index.NeedsLength(key, out var item))
        {
            return;
        }

        if (_lengthJobs.Writer.TryWrite(new LengthJob(_folder, key, Path.Combine(_folder, key), item.Size)))
        {
            _index.LengthQueued(key, item.Size);
        }
    }

    /// <summary>The worker read a length: applied only when the folder, the item and its size are still the same.</summary>
    private void OnLengthRead(LengthRead read)
    {
        if (!string.Equals(read.Folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _index.ApplyLength(read.Key, read.Size, read.Modified, read.Length);
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
        || (path.EndsWith(LibraryPaths.PartExtension, StringComparison.OrdinalIgnoreCase) && _index.IsBeingWritten(path[..^LibraryPaths.PartExtension.Length]))
        || _registered.ContainsKey(path);

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

    // A failed save is retried until it lands, with one warning for each run of failures.
    private void Save()
    {
        try
        {
            LibraryIndexFile.Write(_libraryFile, new LibraryFile(1, _index.Entries(), _folder, _cleanupBlocked));
            if (_saveFailing)
            {
                _saveFailing = false;
                _log?.Invoke("library.json saved after an earlier save failed.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The retry keeps going quietly.
            if (!_saveFailing)
            {
                _failed(e);
            }

            _saveFailing = true;
            _saveRetry ??= _time.CreateTimer(_ => _channel.Writer.TryWrite(new RetrySave()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _saveRetry.Change(SaveRetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed record Registration(long Id, ITimer Timer);

    private sealed record LengthJob(string Folder, string Key, string Path, long Size);
}
