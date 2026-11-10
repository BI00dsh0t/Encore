using System.Threading.Channels;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Library;

/// <summary>The single-threaded Library index: every change, disk check and save runs on its own thread. docs/code-notes.md.</summary>
public sealed partial class LibraryActor : ILibraryWrites, IDisposable
{
    /// <summary>How soon a failed save of the index is tried again.</summary>
    internal static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(1);

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
    private readonly Action<LibraryFolderChanged>? _folderChanged;
    private readonly Func<string, bool>? _recycle;
    private readonly Func<string, string>? _finalPath;
    private readonly Func<CleanupSettings>? _cleanup;
    private readonly Action<string>? _limitUnreachable;
    private readonly Action<string>? _log;
    private readonly Thread _thread;

    // Actor thread only: the items, and what is known of their writes and lengths.
    private readonly LibraryIndex _index;

    // Reads lengths on a thread of its own; null without the port.
    private readonly LibraryLengthWorker? _lengths;

    // Its callbacks run on the watcher's threads and only post.
    private readonly LibraryWatcher _watcher;

    // Actor thread only, each of these.
    private readonly OwnFileOperations _ownOperations;
    private readonly PendingChecks _pending = new();
    private readonly CleanupPass _pass = new();
    private readonly CleanupLimitWarning _limitWarning = new();

    private string _folder = "";
    private ITimer? _saveRetry;
    private bool _saveFailing;
    private bool _cleanupBlocked;
    private bool _dirty;
    private bool _started;
    private DateTimeOffset _startedAt;
    private CleanupSettings _lastCleanup = CleanupOff;
    private ITimer? _cleanupFirst;
    private ITimer? _cleanupPeriodic;
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
        _folderChanged = folderChanged;
        _recycle = recycle;
        _cleanup = cleanup;
        _limitUnreachable = limitUnreachable;
        _log = log;
        _finalPath = finalPath;
        _cleanupBlocked = cleanupBlocked;
        _index = new LibraryIndex(items, added, changed, removed, () => _dirty = true);
        _ownOperations = new OwnFileOperations(time, (path, id) => _channel.Writer.TryWrite(new Expired(path, id)));
        _watcher = new LibraryWatcher(
            (kind, path, oldPath) => _channel.Writer.TryWrite(new Watched(kind, path, oldPath)),
            OnWatcherError,
            failed);

        _channel.Writer.TryWrite(new Start());
        _thread = new Thread(Run) { Name = "LibraryActor", IsBackground = true };
        if (readLength is not null)
        {
            _lengths = new LibraryLengthWorker(readLength, failed, () => Volatile.Read(ref _disposed) != 0, PostLengthRead);
        }

        _thread.Start();
    }

    /// <summary>A test seam: whether the watcher raises events; setting it waits until the actor has applied it.</summary>
    internal bool Watching
    {
        get => _watcher.IsRaising;
        set
        {
            if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            {
                _watcher.SetRaising(value);
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

        _lengths?.Stop();
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
                catch (Exception e) when (LibraryPaths.IsFileFailure(e))
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

        _watcher.Stop();
        _pending.Stop();
        _saveRetry?.Dispose();
        _cleanupFirst?.Dispose();
        _cleanupPeriodic?.Dispose();
        _ownOperations.Clear();
    }

    // Runs on the length worker's thread: it only posts.
    private void PostLengthRead(LengthJob job, DateTime modified, double? length) =>
        _channel.Writer.TryWrite(new LengthRead(job.Folder, job.Key, job.Size, modified, length));

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

    // The gap between Load and the watcher is covered by one rescan; a corrupt load is saved at once.
    private void OnStart()
    {
        _pending.Start(_time, () => _channel.Writer.TryWrite(new Settle()));
        _folder = LibraryPaths.FullFolder(_outputFolder());

        // A length loaded from library.json belongs to the file's current modified time (Load checked it).
        foreach (var item in _index.Items.Where(item => item.LengthSeconds is not null))
        {
            try
            {
                _index.RememberLengthReadAt(item.Path, File.GetLastWriteTimeUtc(Path.Combine(_folder, item.Path)));
            }
            catch (Exception e) when (LibraryPaths.IsFileFailure(e))
            {
                _failed(e);
            }
        }

        CreateMissingFolder();
        _watcher.Start(_folder);
        Rescan();
        foreach (var item in _index.Items.Where(item => !item.BeingWritten && item.LengthSeconds is null).ToList())
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

    /// <summary>Queues a read of the item's length at its current size, unless one is already queued at that size.</summary>
    private void QueueLength(string key)
    {
        if (_lengths is null || !_index.NeedsLength(key, out var item))
        {
            return;
        }

        if (_lengths.TryQueue(new LengthJob(_folder, key, Path.Combine(_folder, key), item.Size)))
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
            _saveRetry ??= _time.CreateTimer(
                _ => _channel.Writer.TryWrite(new RetrySave()),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _saveRetry.Change(SaveRetryDelay, Timeout.InfiniteTimeSpan);
        }
    }
}
