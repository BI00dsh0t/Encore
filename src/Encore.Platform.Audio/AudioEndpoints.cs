using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Encore.Platform.Audio;

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

    /// <summary>The id of the default (eConsole) endpoint of one flow, or <c>null</c> when the flow has none.</summary>
    public static string? DefaultId(AudioFlow flow)
    {
        using var enumerator = Enumerator.Create();
        IMMDevice* device = null;
        try
        {
            enumerator.Value->GetDefaultAudioEndpoint(ToDataFlow(flow), ERole.eConsole, &device);
        }
        catch (Exception e) when (e.HResult == ENotFound)
        {
            return null;
        }

        try
        {
            return ReadId(device);
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

    /// <summary>Matches a query against endpoints: an exact ID or one exact name wins, otherwise one name must contain it.</summary>
    public static EndpointMatch Match(IReadOnlyList<AudioEndpoint> endpoints, string query)
    {
        var exact = endpoints
            .Where(endpoint => string.Equals(endpoint.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpoint.FriendlyName, query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            return new EndpointMatch(EndpointMatchOutcome.Found, exact[0], exact);
        }

        var matches = endpoints.Where(endpoint => endpoint.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
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
        var result = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        if (result.Failed && result != HRESULT.RPC_E_CHANGED_MODE)
        {
            result.ThrowOnFailure();
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
        var idText = ReadId(device);
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

    /// <summary>The device's endpoint id; the string COM allocated for it is freed here.</summary>
    private static string ReadId(IMMDevice* device)
    {
        PWSTR id = default;
        device->GetId(&id);
        try
        {
            return id.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(id.Value);
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
