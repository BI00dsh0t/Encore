using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Encore.Platform.Audio;

/// <summary>
/// Watches the MMDevice endpoints (R1): registers an <c>IMMNotificationClient</c> on its own MTA
/// thread and raises a coalesced callback there when an endpoint is added, removed, changes state
/// or is renamed, or the default communications render endpoint changes. The notification callbacks only set an event; after one, the thread waits
/// <see cref="CoalesceWindow"/>, so a burst raises the callback once.
/// </summary>
internal sealed unsafe class EndpointWatcher : IDisposable
{
    /// <summary>How long the thread gathers notifications before it raises the callback.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private readonly Action _changed;
    private readonly ILogger _log;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private nint _unknown;
    private IMMNotificationClient* _client;
    private int _disposed;

    /// <summary>Starts the thread and returns once the client is registered, or its registration has failed and been logged.</summary>
    public EndpointWatcher(Action changed, ILogger log)
    {
        _changed = changed;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio endpoint watcher" };
        _thread.Start();
        if (!_started.Wait(StartTimeout))
        {
            _log.LogError("The audio endpoint watcher didn't register within {Timeout}", StartTimeout);
        }
    }

    /// <summary>Whether the notification client is registered with MMDevice.</summary>
    public bool Registered { get; private set; }

    /// <summary>The registered client, for tests that call it through its vtable; null when registration failed.</summary>
    internal IMMNotificationClient* Client => _client;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        _thread.Join();
        _signal.Dispose();
        _stop.Dispose();
        _started.Dispose();
    }

    /// <summary>Called from a notification: wakes the thread. Never blocks or throws.</summary>
    internal void Signal()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Run()
    {
        IMMDeviceEnumerator* enumerator = null;
        try
        {
            try
            {
                enumerator = AudioEndpoints.CreateEnumerator();
                _unknown = NotificationWrappers.Instance.GetOrCreateComInterfaceForObject(new NotificationSink(this), CreateComInterfaceFlags.None);
                var iid = IMMNotificationClient.IID_Guid;
                ((HRESULT)Marshal.QueryInterface(_unknown, in iid, out var client)).ThrowOnFailure();
                _client = (IMMNotificationClient*)client;
                enumerator->RegisterEndpointNotificationCallback(_client);
                Registered = true;
                _log.LogInformation("Watching audio endpoints");
            }
            catch (Exception e)
            {
                _log.LogError(e, "Couldn't watch audio endpoints; the device list won't follow changes");
            }
            finally
            {
                _started.Set();
            }

            if (Registered)
            {
                Watch();
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    private void Watch()
    {
        WaitHandle[] handles = [_stop, _signal];
        while (WaitHandle.WaitAny(handles) == 1)
        {
            // Gather the rest of a burst; a stop during it ends the thread.
            if (_stop.WaitOne(CoalesceWindow))
            {
                return;
            }

            _signal.Reset();
            try
            {
                _changed();
            }
            catch (Exception e)
            {
                _log.LogError(e, "An audio endpoint change handler failed");
            }
        }
    }

    private void Release(IMMDeviceEnumerator* enumerator)
    {
        if (Registered)
        {
            try
            {
                enumerator->UnregisterEndpointNotificationCallback(_client);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Couldn't unregister the audio endpoint watcher");
            }

            Registered = false;
        }

        if (_client is not null)
        {
            _client->Release();
            _client = null;
        }

        if (_unknown != 0)
        {
            Marshal.Release(_unknown);
            _unknown = 0;
        }

        if (enumerator is not null)
        {
            enumerator->Release();
        }
    }

    /// <summary>The managed side of the CCW: every notification that can change the list wakes the watcher.</summary>
    private sealed class NotificationSink(EndpointWatcher watcher) : IMMNotificationClient.Interface
    {
        private static readonly HRESULT Ok = (HRESULT)0;

        public HRESULT OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) => Signal();

        public HRESULT OnDeviceAdded(PCWSTR pwstrDeviceId) => Signal();

        public HRESULT OnDeviceRemoved(PCWSTR pwstrDeviceId) => Signal();

        // The list marks each flow's console default (story 10.4), and the default communications
        // render endpoint is where Feedback Sounds play, so the capture check re-runs.
        public HRESULT OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId) =>
            role == ERole.eConsole || (flow == EDataFlow.eRender && role == ERole.eCommunications) ? Signal() : Ok;

        // Only the two names the list shows; the store raises many other keys.
        public HRESULT OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key) =>
            Is(key, PInvoke.PKEY_Device_FriendlyName) || Is(key, PInvoke.PKEY_DeviceInterface_FriendlyName) ? Signal() : Ok;

        private static bool Is(PROPERTYKEY key, PROPERTYKEY other) => key.fmtid == other.fmtid && key.pid == other.pid;

        private HRESULT Signal()
        {
            watcher.Signal();
            return Ok;
        }
    }

    /// <summary>Builds the CCW for <see cref="NotificationSink"/>: IUnknown plus <c>IMMNotificationClient</c>.</summary>
    private sealed class NotificationWrappers : ComWrappers
    {
        public static readonly NotificationWrappers Instance = new();

        private static readonly ComInterfaceEntry* Entries = CreateEntries();

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            count = 1;
            return Entries;
        }

        protected override object CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();

        protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();

        private static ComInterfaceEntry* CreateEntries()
        {
            GetIUnknownImpl(out var queryInterface, out var addRef, out var release);
            var vtable = (IMMNotificationClient.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(NotificationWrappers), sizeof(IMMNotificationClient.Vtbl));
            vtable->QueryInterface_1 = (delegate* unmanaged[Stdcall]<IMMNotificationClient*, Guid*, void**, HRESULT>)queryInterface;
            vtable->AddRef_2 = (delegate* unmanaged[Stdcall]<IMMNotificationClient*, uint>)addRef;
            vtable->Release_3 = (delegate* unmanaged[Stdcall]<IMMNotificationClient*, uint>)release;
            IMMNotificationClient.PopulateVTable(vtable);

            var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(NotificationWrappers), sizeof(ComInterfaceEntry));
            entries->IID = IMMNotificationClient.IID_Guid;
            entries->Vtable = (nint)vtable;
            return entries;
        }
    }
}
