using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Engine.Ipc;

/// <summary>Where the hub delivers frames: one per connection.</summary>
internal interface IFrameSink
{
    /// <summary>Queues a frame without blocking; a sink that can't take it drops itself.</summary>
    void Enqueue(byte[] frame, string? coalesceKey);
}

/// <summary>The engine's published state: one lock assigns <c>seq</c>, applies the event and hands it to every connection.</summary>
internal sealed class EventHub
{
    private readonly Lock _lock = new();
    private readonly List<IFrameSink> _sinks = [];
    private StateSnapshot _state;

    /// <summary>The optional arguments are the state the engine starts with: in the first snapshot, so no event carries them.</summary>
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

    /// <summary>Publishes an event to every connection and returns its <c>seq</c>.</summary>
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

    /// <summary>For <c>dev.simulate engineHang</c> only: a thread takes the hub's lock for good, so every publish blocks.</summary>
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
