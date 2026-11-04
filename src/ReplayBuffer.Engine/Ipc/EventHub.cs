using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Engine.Ipc;

/// <summary>Where the hub delivers frames: one per connection.</summary>
internal interface IFrameSink
{
    /// <summary>Queues a frame without blocking; a sink that can't take it drops itself.</summary>
    void Enqueue(byte[] frame, string? coalesceKey);
}

/// <summary>
/// The engine's published state (AD-3). One lock assigns <c>seq</c>, applies each event to the
/// state, and hands the frame to every connection, so a snapshot built under it is followed by
/// exactly the events after it. Publishing never waits on a client.
/// </summary>
internal sealed class EventHub
{
    private readonly Lock _lock = new();
    private readonly List<IFrameSink> _sinks = [];
    private StateSnapshot _state;

    /// <param name="devices">The device list the engine starts with; in the initial state, so no event and no <c>seq</c>.</param>
    /// <param name="library">The Library items the engine starts with; in the initial state likewise.</param>
    /// <param name="cleanupBlocked">Whether Cleanup starts blocked by a rebuilt index; in the initial state likewise.</param>
    /// <param name="libraryFolder">The absolute Output Folder the Library indexes; in the initial state likewise.</param>
    /// <param name="estimate">The storage estimate the engine starts with; in the initial state likewise.</param>
    public EventHub(SettingsDocument settings, AudioDeviceList? devices = null, IReadOnlyList<LibraryItem>? library = null, bool cleanupBlocked = false, string? libraryFolder = null, StorageEstimate? estimate = null)
    {
        _state = StateSnapshot.Initial(Guid.NewGuid().ToString("N"), Environment.ProcessId, settings) with
        {
            Devices = devices ?? AudioDeviceList.Empty,
            Library = library ?? [],
            CleanupBlocked = cleanupBlocked,
            Estimate = estimate,
            LibraryFolder = libraryFolder,
        };
    }

    /// <summary>The current state, with the <c>seq</c> of the last event in it.</summary>
    public StateSnapshot State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Publishes an event to every connection.</summary>
    /// <returns>The event's <c>seq</c>.</returns>
    public long Publish<T>(EventType<T> type, T payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_lock)
        {
            var seq = _state.Seq + 1;
            _state = type.Apply(_state, payload) with { Seq = seq };
            var frame = FrameWriter.Event(type.Name, seq, payload, type.PayloadType);
            var key = type.Coalesces ? type.Name : null;
            foreach (var sink in _sinks)
            {
                sink.Enqueue(frame, key);
            }

            return seq;
        }
    }

    /// <summary>Queues the snapshot to <paramref name="sink"/> as its first frame, then subscribes it.</summary>
    public void Attach(IFrameSink sink)
    {
        lock (_lock)
        {
            sink.Enqueue(FrameWriter.Event(EngineMessages.StateSnapshotType, _state.Seq, _state, EngineJsonContext.Default.StateSnapshot), null);
            _sinks.Add(sink);
        }
    }

    public void Detach(IFrameSink sink)
    {
        lock (_lock)
        {
            _sinks.Remove(sink);
        }
    }

    /// <summary>
    /// <c>dev.simulate engineHang</c> only: a background thread takes the hub's lock and never lets it go,
    /// so every publish, snapshot and attach after this blocks, as in a real hang. Returns once it holds it.
    /// </summary>
    public void Freeze()
    {
        using var held = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            lock (_lock)
            {
                // Set inside the lock; the caller waits for it, so it is never disposed before this runs.
                held.Set();
                Thread.Sleep(Timeout.Infinite);
            }
        })
        {
            IsBackground = true,
            Name = "EventHub freeze",
        };
        thread.Start();
        held.Wait();
    }
}
