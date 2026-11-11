using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Encore.Engine.Ipc;

namespace Encore.Engine.Hosting;

/// <summary>Publishes the monitor list when the attached monitors change, so the picker follows them.</summary>
internal sealed class MonitorListPublisher(IDisplayMonitors displays, EventHub hub)
{
    // Changes come one at a time, on the source's thread.
    private MonitorList? _last;

    public IDisposable Subscribe() => displays.Subscribe(Publish);

    private void Publish()
    {
        var monitors = new MonitorList(displays.List());
        if (_last is not null && _last.Monitors.SequenceEqual(monitors.Monitors))
        {
            return;
        }

        _last = monitors;
        hub.Publish(RecordingMessages.MonitorsChanged, monitors);
    }
}
