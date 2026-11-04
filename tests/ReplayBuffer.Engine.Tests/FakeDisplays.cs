using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Engine.Tests;

/// <summary>A display port the test changes, raising each change on another thread as the window's would.</summary>
internal sealed class FakeDisplays(IReadOnlyList<DisplayMonitor> monitors) : IDisplayMonitors
{
    private volatile IReadOnlyList<DisplayMonitor> _monitors = monitors;
    private readonly List<Action> _changed = [];

    public IReadOnlyList<DisplayMonitor> List() => _monitors;

    public IDisposable Subscribe(Action changed)
    {
        lock (_changed)
        {
            _changed.Add(changed);
        }

        return new Unsubscribe(this, changed);
    }

    public void Change(IReadOnlyList<DisplayMonitor> monitors)
    {
        _monitors = monitors;
        Action[] subscribers;
        lock (_changed)
        {
            subscribers = [.. _changed];
        }

        Task.Run(() => Array.ForEach(subscribers, changed => changed())).Wait(TestContext.Current.CancellationToken);
    }

    private sealed class Unsubscribe(FakeDisplays owner, Action changed) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._changed)
            {
                owner._changed.Remove(changed);
            }
        }
    }
}
