using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Platform.Audio.Tests;

/// <summary>
/// The hub's endpoint watcher on this PC. Nothing here changes a real device: the notifications
/// are made by calling the registered client through its <c>IMMNotificationClient</c> vtable.
/// </summary>
public sealed unsafe class EndpointWatcherTests : IDisposable
{
    private const string FakeId = "{0.0.0.00000000}.{00000000-0000-0000-0000-0000000000aa}";
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    private readonly AudioDeviceHub _hub = new(NullLoggerFactory.Instance);
    private int _raised;

    public EndpointWatcherTests() => _hub.EndpointsChanged += OnChanged;

    public void Dispose() => _hub.Dispose();

    private IMMNotificationClient* Client
    {
        get
        {
            var watcher = _hub.Watcher;
            Assert.NotNull(watcher);
            Assert.True(watcher.Registered, "the endpoint watcher didn't register");
            var client = watcher.Client;
            Assert.True(client is not null, "the endpoint watcher has no client");
            return client;
        }
    }

    [Fact]
    public void TheHubStartsAndStopsWatchingCleanly()
    {
        var watcher = _hub.Watcher;
        Assert.NotNull(watcher);
        Assert.True(watcher.Registered);
        Assert.True(watcher.Client is not null);

        _hub.Dispose();

        Assert.False(watcher.Registered);
        Assert.True(watcher.Client is null);
        Assert.Null(_hub.Watcher);
    }

    [Fact]
    public void ListPresentIsEveryActiveEndpointOfBothFlows()
    {
        var expected = AudioEndpoints.List(AudioFlow.Output).Select(e => (e.Id, e.FriendlyName, e.ParentName, SettingsFlow.Output))
            .Concat(AudioEndpoints.List(AudioFlow.Input).Select(e => (e.Id, e.FriendlyName, e.ParentName, SettingsFlow.Input)));

        var present = _hub.ListPresent();

        Assert.Equal(expected, present.Select(d => (d.EndpointId, d.FriendlyName, d.ParentName, d.Flow)));
        Assert.All(present, d => Assert.True(d.Available));
    }

    [Fact]
    public void AnAddedDeviceRaisesChanged()
    {
        Client->OnDeviceAdded(FakeId);

        Assert.True(WaitFor(1, Within), "no change after OnDeviceAdded");
    }

    [Fact]
    public void AStateChangeOrRemovalRaisesChanged()
    {
        Client->OnDeviceStateChanged(FakeId, DEVICE_STATE.DEVICE_STATE_DISABLED);
        Assert.True(WaitFor(1, Within), "no change after OnDeviceStateChanged");

        Client->OnDeviceRemoved(FakeId);
        Assert.True(WaitFor(2, Within), "no change after OnDeviceRemoved");
    }

    [Fact]
    public void ABurstRaisesOnce()
    {
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            Client->OnDeviceAdded(FakeId);
        }

        Assert.InRange(clock.ElapsedMilliseconds, 0, 99);
        Assert.True(WaitFor(1, Within), "no change after a burst");
        Thread.Sleep(Quiet);
        Assert.Equal(1, Volatile.Read(ref _raised));
    }

    [Fact]
    public void AConsoleDefaultChangeRaisesChanged()
    {
        // Story 10.4: the list marks each flow's console default.
        Client->OnDefaultDeviceChanged(EDataFlow.eCapture, ERole.eConsole, FakeId);

        Assert.True(WaitFor(1, Within), "no change after a default change");
    }

    [Fact]
    public void AnUnrelatedPropertyOrAMultimediaDefaultChangeRaisesNothing()
    {
        // The same fmtid as the friendly name, another pid; and an unrelated fmtid with its pid.
        var name = PInvoke.PKEY_Device_FriendlyName;
        Client->OnPropertyValueChanged(FakeId, new PROPERTYKEY { fmtid = name.fmtid, pid = name.pid + 1 });
        Client->OnPropertyValueChanged(FakeId, new PROPERTYKEY { fmtid = Guid.NewGuid(), pid = name.pid });
        Client->OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eMultimedia, FakeId);

        Thread.Sleep(Quiet);
        Assert.Equal(0, Volatile.Read(ref _raised));
    }

    private void OnChanged() => Interlocked.Increment(ref _raised);

    private bool WaitFor(int count, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _raised) < count)
        {
            if (clock.Elapsed > timeout)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }
}
