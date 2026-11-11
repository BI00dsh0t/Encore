using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Replay;
using Encore.Core.Settings;

namespace Encore.Engine.Sessions;

/// <summary>Keeps the Global settings' storage estimate, re-checked on settings changes and every 30 s. docs/code-notes.md.</summary>
internal sealed class EstimatePublisher : IDisposable
{
    /// <summary>How often the free bytes are read again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly Func<SettingsDocument> _settings;
    private readonly IMemoryStatus _memory;
    private readonly Func<string, long?> _freeDisk;
    private readonly string _defaultRingFolder;
    private readonly Action<StorageEstimate> _publish;
    private readonly ITimer _timer;
    private StorageEstimate? _last;
    private long _lastRevision = long.MinValue;
    private int _disposed;

    public EstimatePublisher(
        Func<SettingsDocument> settings,
        IMemoryStatus memory,
        Func<string, long?> freeDisk,
        string defaultRingFolder,
        Action<StorageEstimate> publish,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _settings = settings;
        _memory = memory;
        _freeDisk = freeDisk;
        _defaultRingFolder = defaultRingFolder;
        _publish = publish;
        _timer = time.CreateTimer(_ => TryRefresh(null), null, RecheckInterval, RecheckInterval);
    }

    /// <summary>The estimate of <paramref name="document"/>'s Global settings against the free bytes now.</summary>
    public StorageEstimate Current(SettingsDocument document)
    {
        var global = SettingsResolver.Resolve(document, null);
        var folder = global.RingPath ?? _defaultRingFolder;
        long? free = global.ReplayStorage == ReplayStorage.Memory
            ? _memory.Read() is { TotalBytes: > 0 } status ? status.AvailableBytes + Environment.WorkingSet : null
            : _freeDisk(folder) + RingFileBytes(folder);
        return ReplayStorageEstimate.For(global, free);
    }

    /// <summary>The first estimate, recorded as the last one but not published.</summary>
    public StorageEstimate Seed()
    {
        var document = _settings();
        var estimate = Current(document);
        lock (_lock)
        {
            _last = estimate;
            _lastRevision = document.Revision;
        }

        return estimate;
    }

    /// <summary>The settings changed: re-estimates from <paramref name="document"/> on the thread pool, off the updating thread.</summary>
    public void OnSettingsChanged(SettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThreadPool.QueueUserWorkItem(_ => TryRefresh(document));
    }

    /// <summary>Re-estimates from the current settings, publishing when it differs enough from the last.</summary>
    public void Refresh() => Refresh(_settings());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
        }
    }

    /// <summary>Free disk space of the folder's drive; null when it can't be read.</summary>
    public static long? FreeDiskBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            return null;
        }
    }

    // The ring files' bytes in the folder, which the running ring has taken from the drive's free space.
    private static long RingFileBytes(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles(RingFile.Prefix + "*.bin")
                .Where(IsRingFile)
                .Sum(f => f.Length);
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            return 0;
        }
    }

    // A ring file is named by the prefix and a number.
    private static bool IsRingFile(FileInfo file) =>
        file.Extension == ".bin"
        && Path.GetFileNameWithoutExtension(file.Name)[RingFile.Prefix.Length..] is { Length: > 0 } number
        && number.All(char.IsAsciiDigit);

    // A folder or drive that can't be read, for any of the reasons the file system gives.
    private static bool IsUnreadable(Exception e) =>
        e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException;

    private static bool Differs(StorageEstimate last, StorageEstimate next) =>
        last.Storage != next.Storage
        || last.Bytes != next.Bytes
        || last.WorstCase != next.WorstCase
        || last.ExceedsFree != next.ExceedsFree
        || (last.FreeBytes, next.FreeBytes) switch
        {
            (null, null) => false,
            ({ } a, { } b) => a == 0 ? b != 0 : Math.Abs(b - a) >= a / 10.0,
            _ => true,
        };

    // A timer or thread-pool refresh: nothing escapes to the timer or the pool.
    private void TryRefresh(SettingsDocument? document)
    {
        try
        {
            Refresh(document ?? _settings());
        }
        catch (Exception)
        {
            // The next change or re-check tries again.
        }
    }

    private void Refresh(SettingsDocument document)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Reads memory and the drive outside the lock; publishing inside it keeps events in order.
        var next = Current(document);
        lock (_lock)
        {
            // A refresh that read an older document than the last estimate's finished late.
            if (document.Revision < _lastRevision)
            {
                return;
            }

            _lastRevision = document.Revision;
            if (_last is not null && !Differs(_last, next))
            {
                return;
            }

            _last = next;
            _publish(next);
        }
    }
}
