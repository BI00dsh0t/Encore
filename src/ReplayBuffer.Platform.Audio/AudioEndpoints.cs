using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace ReplayBuffer.Platform.Audio;

/// <summary>Output Devices are captured by loopback, Input Devices by capture (AD-13).</summary>
public enum AudioFlow
{
    Output,
    Input,
}

/// <summary>
/// An active MMDevice endpoint. <see cref="ParentName"/> is the name of the audio device that
/// exposes it (<c>PKEY_DeviceInterface_FriendlyName</c>), or empty when it has none.
/// </summary>
public sealed record AudioEndpoint(string Id, string FriendlyName, AudioFlow Flow, string ParentName = "");

public enum EndpointMatchOutcome
{
    Found,
    NotFound,
    Ambiguous,
}

/// <summary>
/// The result of matching a device name: the endpoint when exactly one matched, and
/// otherwise the candidates to list (every endpoint when none matched, the matches when several did).
/// </summary>
public sealed record EndpointMatch(EndpointMatchOutcome Outcome, AudioEndpoint? Endpoint, IReadOnlyList<AudioEndpoint> Candidates);

/// <summary>Lists and resolves audio endpoints.</summary>
public static unsafe class AudioEndpoints
{
    /// <summary><c>E_NOTFOUND</c>, <c>HRESULT_FROM_WIN32(ERROR_NOT_FOUND)</c>: no endpoint has that role.</summary>
    private const int ENotFound = unchecked((int)0x80070490);

    /// <summary>The active endpoints of one flow.</summary>
    public static IReadOnlyList<AudioEndpoint> List(AudioFlow flow)
    {
        using var enumerator = Enumerator.Create();
        IMMDeviceCollection* collection = null;
        enumerator.Value->EnumAudioEndpoints(ToDataFlow(flow), DEVICE_STATE.DEVICE_STATE_ACTIVE, &collection);
        try
        {
            uint count;
            collection->GetCount(&count);
            var endpoints = new List<AudioEndpoint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device = null;
                collection->Item(i, &device);
                try
                {
                    endpoints.Add(Describe(device, flow));
                }
                finally
                {
                    device->Release();
                }
            }

            return endpoints;
        }
        finally
        {
            collection->Release();
        }
    }

    /// <summary>The default (eConsole) endpoint of one flow.</summary>
    public static AudioEndpoint Default(AudioFlow flow)
    {
        using var enumerator = Enumerator.Create();
        IMMDevice* device = null;
        enumerator.Value->GetDefaultAudioEndpoint(ToDataFlow(flow), ERole.eConsole, &device);
        try
        {
            return Describe(device, flow);
        }
        finally
        {
            device->Release();
        }
    }

    /// <summary>The default communications (eCommunications) render endpoint, or <c>null</c> when there is none.</summary>
    public static AudioEndpoint? DefaultCommunications()
    {
        using var enumerator = Enumerator.Create();
        IMMDevice* device = null;
        try
        {
            enumerator.Value->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eCommunications, &device);
        }
        catch (Exception e) when (e.HResult == ENotFound)
        {
            return null;
        }

        try
        {
            return Describe(device, AudioFlow.Output);
        }
        finally
        {
            device->Release();
        }
    }

    /// <summary>
    /// Matches <paramref name="query"/> against endpoints: an exact endpoint ID or a single
    /// exact friendly name (ignoring case) wins, otherwise the name must contain the query exactly once.
    /// </summary>
    public static EndpointMatch Match(IReadOnlyList<AudioEndpoint> endpoints, string query)
    {
        var exact = endpoints
            .Where(e => string.Equals(e.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.FriendlyName, query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            return new EndpointMatch(EndpointMatchOutcome.Found, exact[0], exact);
        }

        var matches = endpoints.Where(e => e.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            0 => new EndpointMatch(EndpointMatchOutcome.NotFound, null, endpoints),
            1 => new EndpointMatch(EndpointMatchOutcome.Found, matches[0], matches),
            _ => new EndpointMatch(EndpointMatchOutcome.Ambiguous, null, matches),
        };
    }

    /// <summary>Opens an endpoint by ID. The caller releases the device.</summary>
    internal static IMMDevice* OpenDevice(string id)
    {
        using var enumerator = Enumerator.Create();
        IMMDevice* device = null;
        enumerator.Value->GetDevice(id, &device);
        return device;
    }

    /// <summary>Joins the MTA if the thread has no apartment yet; an STA thread is left alone.</summary>
    internal static void EnsureCom()
    {
        var hr = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        if (hr.Failed && hr != HRESULT.RPC_E_CHANGED_MODE)
        {
            hr.ThrowOnFailure();
        }
    }

    /// <summary>Creates an MMDevice enumerator, joining the MTA first if needed. The caller releases it.</summary>
    internal static IMMDeviceEnumerator* CreateEnumerator()
    {
        EnsureCom();
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = IMMDeviceEnumerator.IID_Guid;
        IMMDeviceEnumerator* value = null;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iid, (void**)&value).ThrowOnFailure();
        return value;
    }

    private static EDataFlow ToDataFlow(AudioFlow flow) => flow == AudioFlow.Output ? EDataFlow.eRender : EDataFlow.eCapture;

    internal static AudioEndpoint Describe(IMMDevice* device, AudioFlow flow)
    {
        PWSTR id = default;
        device->GetId(&id);
        string idText;
        try
        {
            idText = id.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(id.Value);
        }

        IPropertyStore* store = null;
        device->OpenPropertyStore(STGM.STGM_READ, &store);
        try
        {
            var name = ReadString(store, PInvoke.PKEY_Device_FriendlyName) ?? idText;
            var parent = ReadString(store, PInvoke.PKEY_DeviceInterface_FriendlyName) ?? "";
            return new AudioEndpoint(idText, name, flow, parent);
        }
        finally
        {
            store->Release();
        }
    }

    /// <summary>A string property, or null when it is missing or not a string.</summary>
    private static string? ReadString(IPropertyStore* store, PROPERTYKEY key)
    {
        // A property the store doesn't hold reads as VT_EMPTY.
        PROPVARIANT value = default;
        store->GetValue(&key, &value);
        try
        {
            return value.Anonymous.Anonymous.vt == VARENUM.VT_LPWSTR
                ? value.Anonymous.Anonymous.Anonymous.pwszVal.ToString()
                : null;
        }
        finally
        {
            PInvoke.PropVariantClear(&value);
        }
    }

    private readonly ref struct Enumerator
    {
        private Enumerator(IMMDeviceEnumerator* value) => Value = value;

        public IMMDeviceEnumerator* Value { get; }

        public static Enumerator Create() => new(CreateEnumerator());

        public void Dispose() => Value->Release();
    }
}
