using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using IInspectable = Windows.Win32.System.WinRT.IInspectable;
using WinRT;

namespace Encore.Platform.Capture;

/// <summary>
/// The engine's one D3D11 device (AD-7), on the adapter that owns the game window's monitor.
/// Multithread-protected, because WGC, the engine and NVENC all use its immediate context.
/// A GPU driver reset removes it (<see cref="IsLost"/>); its calls then fail with
/// <see cref="DeviceRemoved"/> or <see cref="DeviceReset"/>, and only a new device works again.
/// </summary>
public sealed unsafe class D3D11Device : IDisposable
{
    /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c>: the GPU was physically removed, or its driver was updated or crashed.</summary>
    public const int DeviceRemoved = unchecked((int)0x887A0005);

    /// <summary><c>DXGI_ERROR_DEVICE_RESET</c>: the GPU driver reset the device (a TDR).</summary>
    public const int DeviceReset = unchecked((int)0x887A0007);

    // The number of simulated resets so far (SimulateReset): a device created before the latest is lost.
    private static long s_resets;

    private readonly long _resetsAtCreate;
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private volatile bool _simulatedLoss;

    private D3D11Device(ID3D11Device* device, ID3D11DeviceContext* context, IDirect3DDevice winRtDevice, string adapterName)
    {
        _device = device;
        _context = context;
        WinRTDevice = winRtDevice;
        AdapterName = adapterName;
        _resetsAtCreate = Interlocked.Read(ref s_resets);
    }

    /// <summary>
    /// Why the device was removed (<c>GetDeviceRemovedReason</c>): S_OK (0) while it works, a failed
    /// HRESULT such as <see cref="DeviceReset"/> once it is lost. A simulated loss reads as
    /// <see cref="DeviceReset"/>. A disposed device reads as S_OK.
    /// </summary>
    public int RemovedReason
    {
        get
        {
            if (IsSimulatedLoss)
            {
                return DeviceReset;
            }

            var device = _device;
            if (device is null)
            {
                return 0;
            }

            // CsWin32 turns the failed HRESULT into an exception carrying it.
            try
            {
                device->GetDeviceRemovedReason();
                return 0;
            }
            catch (Exception e)
            {
                return e.HResult;
            }
        }
    }

    /// <summary>Whether the device was removed or reset, so every call on it fails until a new one replaces it.</summary>
    public bool IsLost => RemovedReason < 0;

    private bool IsSimulatedLoss => _simulatedLoss || Interlocked.Read(ref s_resets) > _resetsAtCreate;

    /// <summary>
    /// The fault seam (tests and <c>dev.simulate gpuReset</c>, never a real TDR): this device reports
    /// itself lost from now on, and its next draw fails as a reset device's would.
    /// </summary>
    public void SimulateLoss() => _simulatedLoss = true;

    /// <summary>The fault seam for every device: each one created before this call reports itself lost, as after a driver reset.</summary>
    public static void SimulateReset() => Interlocked.Increment(ref s_resets);

    /// <summary>Throws what a reset device's call would (<see cref="DeviceReset"/>) when the loss is simulated.</summary>
    public void ThrowIfSimulatedLoss()
    {
        if (IsSimulatedLoss)
        {
            throw new COMException("The GPU device was reset (simulated).", DeviceReset);
        }
    }

    /// <summary>The raw <c>ID3D11Device*</c>, owned by this object.</summary>
    public nint NativeDevice => (nint)_device;

    internal ID3D11Device* Device => _device;

    internal ID3D11DeviceContext* Context => _context;

    /// <summary>The WinRT wrapper WGC frame pools are created on.</summary>
    public IDirect3DDevice WinRTDevice { get; }

    public string AdapterName { get; }

    /// <summary>
    /// Creates the device on the adapter whose output shows <paramref name="hwnd"/>, or on
    /// adapter 0 when no adapter owns that monitor or <paramref name="hwnd"/> is zero.
    /// </summary>
    public static D3D11Device Create(nint hwnd, ILogger logger)
    {
        var monitor = hwnd == 0
            ? default
            : PInvoke.MonitorFromWindow(new HWND((void*)hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return Create(monitor, logger);
    }

    /// <summary>
    /// Creates the device on the adapter whose output is <paramref name="hmonitor"/> (a Monitor
    /// Recording's monitor, 6.9), or on adapter 0 when no adapter owns it or it is zero.
    /// </summary>
    public static D3D11Device CreateForMonitor(nint hmonitor, ILogger logger) => Create(new HMONITOR((void*)hmonitor), logger);

    private static D3D11Device Create(HMONITOR monitor, ILogger logger)
    {
        IDXGIFactory1* factory = null;
        IDXGIAdapter1* adapter = null;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        try
        {
            var factoryIid = IDXGIFactory1.IID_Guid;
            PInvoke.CreateDXGIFactory1(&factoryIid, (void**)&factory).ThrowOnFailure();
            adapter = PickAdapter(factory, monitor);

            var desc = adapter->GetDesc();
            var adapterName = desc.Description.AsReadOnlySpan().SliceAtNull().ToString();

            ReadOnlySpan<D3D_FEATURE_LEVEL> levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
            PInvoke.D3D11CreateDevice(
                (IDXGIAdapter*)adapter,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                default,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                levels,
                PInvoke.D3D11_SDK_VERSION,
                &device,
                out var featureLevel,
                &context).ThrowOnFailure();

            ID3D10Multithread* multithread = null;
            var multithreadIid = ID3D10Multithread.IID_Guid;
            ((IUnknown*)device)->QueryInterface(&multithreadIid, (void**)&multithread).ThrowOnFailure();
            multithread->SetMultithreadProtected(true);
            multithread->Release();

            var winRtDevice = CreateWinRTDevice(device);
            logger.LogInformation("D3D11 device on {Adapter}, feature level {FeatureLevel}", adapterName, featureLevel);

            var created = new D3D11Device(device, context, winRtDevice, adapterName);
            device = null;
            context = null;
            return created;
        }
        finally
        {
            if (context is not null) context->Release();
            if (device is not null) device->Release();
            if (adapter is not null) adapter->Release();
            if (factory is not null) factory->Release();
        }
    }

    /// <summary>
    /// Copies a captured frame into slice <paramref name="targetSubresource"/> of
    /// <paramref name="target"/>. When the content doesn't fill the target, the target is
    /// cleared to black first and only the overlap is copied: a resize clips, never scales.
    /// </summary>
    public void CopyFrame(nint source, VideoSize contentSize, nint target, uint targetSubresource, VideoSize targetSize)
    {
        var src = (ID3D11Texture2D*)source;
        src->GetDesc(out var srcDesc);
        var copy = contentSize.Intersect(targetSize).Intersect(new VideoSize((int)srcDesc.Width, (int)srcDesc.Height));

        if (copy != targetSize)
        {
            Clear(target, targetSubresource, 0f, 0f, 0f);
        }

        var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = (uint)copy.Width, bottom = (uint)copy.Height, back = 1 };
        _context->CopySubresourceRegion((ID3D11Resource*)target, targetSubresource, 0, 0, 0, (ID3D11Resource*)src, 0, &box);
    }

    /// <summary>
    /// Spike 11.1: copies a captured frame into <paramref name="target"/> (slice 0) with its top-left
    /// at (<paramref name="x"/>, <paramref name="y"/>), clipped to <paramref name="area"/>, the
    /// monitor's place in a composited canvas. Same format only; a resize clips, never scales.
    /// </summary>
    public void CopyFrameTo(nint source, VideoSize contentSize, nint target, int x, int y, VideoSize area)
    {
        var src = (ID3D11Texture2D*)source;
        src->GetDesc(out var srcDesc);
        var copy = contentSize.Intersect(area).Intersect(new VideoSize((int)srcDesc.Width, (int)srcDesc.Height));
        var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = (uint)copy.Width, bottom = (uint)copy.Height, back = 1 };
        _context->CopySubresourceRegion((ID3D11Resource*)target, 0, (uint)x, (uint)y, 0, (ID3D11Resource*)src, 0, &box);
    }

    /// <summary>
    /// Spike 11.1: the GPU time, in microseconds, of one <see cref="CopyFrameTo"/> of all of
    /// <paramref name="source"/> into <paramref name="target"/> at (<paramref name="x"/>, <paramref name="y"/>),
    /// averaged over <paramref name="count"/> copies between two timestamp queries. Null when the
    /// timestamps were disjoint. Blocks until the GPU has run them.
    /// </summary>
    public double? TimeCopies(nint source, nint target, int x, int y, int count)
    {
        ((ID3D11Texture2D*)source)->GetDesc(out var desc);
        var size = new VideoSize((int)desc.Width, (int)desc.Height);
        ID3D11Query* disjoint = null, begin = null, end = null;
        try
        {
            var disjointDesc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT };
            var stampDesc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP };
            _device->CreateQuery(&disjointDesc, &disjoint);
            _device->CreateQuery(&stampDesc, &begin);
            _device->CreateQuery(&stampDesc, &end);

            _context->Begin((ID3D11Asynchronous*)disjoint);
            _context->End((ID3D11Asynchronous*)begin);
            for (var i = 0; i < count; i++)
            {
                CopyFrameTo(source, size, target, x, y, size);
            }

            _context->End((ID3D11Asynchronous*)end);
            _context->End((ID3D11Asynchronous*)disjoint);

            TimestampDisjoint data;
            while (GetData((ID3D11Asynchronous*)disjoint, &data, (uint)sizeof(TimestampDisjoint)) != HRESULT.S_OK)
            {
                Thread.Yield();
            }

            ulong t0, t1;
            while (GetData((ID3D11Asynchronous*)begin, &t0, sizeof(ulong)) != HRESULT.S_OK)
            {
                Thread.Yield();
            }

            while (GetData((ID3D11Asynchronous*)end, &t1, sizeof(ulong)) != HRESULT.S_OK)
            {
                Thread.Yield();
            }

            return data.Disjoint != 0 ? null : (t1 - t0) * 1e6 / data.Frequency / count;
        }
        finally
        {
            if (end is not null) ((IUnknown*)end)->Release();
            if (begin is not null) ((IUnknown*)begin)->Release();
            if (disjoint is not null) ((IUnknown*)disjoint)->Release();
        }
    }

    /// <summary>
    /// <c>ID3D11DeviceContext::GetData</c> by its vtable slot, for its HRESULT: S_FALSE (not ready
    /// yet) is a success CsWin32's wrapper can't tell from S_OK.
    /// </summary>
    private HRESULT GetData(ID3D11Asynchronous* query, void* data, uint size)
    {
        const int getDataSlot = 29; // IUnknown 3, ID3D11DeviceChild 4, then ID3D11DeviceContext's 22 before it
        var context = _context;
        return ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, ID3D11Asynchronous*, void*, uint, uint, HRESULT>)
            (*(void***)context)[getDataSlot])(context, query, data, size, 0);
    }

    /// <summary>D3D11_QUERY_DATA_TIMESTAMP_DISJOINT.</summary>
    private struct TimestampDisjoint
    {
        public ulong Frequency;
        public int Disjoint;
    }

    /// <summary>Submits the queued GPU work, as an encode of the copied frame would.</summary>
    public void Flush() => _context->Flush();

    /// <summary>Fills slice <paramref name="subresource"/> of a render-target texture with one colour.</summary>
    public void Clear(nint target, uint subresource, float red, float green, float blue)
    {
        var texture = (ID3D11Texture2D*)target;
        texture->GetDesc(out var desc);

        var view = new D3D11_RENDER_TARGET_VIEW_DESC { Format = desc.Format };
        if (desc.ArraySize > 1)
        {
            view.ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
            view.Anonymous.Texture2DArray = new D3D11_TEX2D_ARRAY_RTV { MipSlice = 0, FirstArraySlice = subresource, ArraySize = 1 };
        }
        else
        {
            view.ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D;
        }

        ID3D11RenderTargetView* rtv = null;
        _device->CreateRenderTargetView((ID3D11Resource*)texture, &view, &rtv);
        try
        {
            var color = stackalloc float[4] { red, green, blue, 1f };
            _context->ClearRenderTargetView(rtv, color);
        }
        finally
        {
            rtv->Release();
        }
    }

    /// <summary>
    /// Sets the GPU thread priority of this device (<c>IDXGIDevice::SetGPUThreadPriority</c>,
    /// −7 to 7; no elevation needed) and returns the HRESULT. <paramref name="readBack"/> is
    /// the priority the device reports afterwards, or null when it can't be read.
    /// </summary>
    public int SetGpuThreadPriority(int priority, out int? readBack)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(priority, -7);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 7);
        readBack = null;

        IDXGIDevice* dxgiDevice = null;
        try
        {
            var dxgiIid = IDXGIDevice.IID_Guid;
            var hr = ((IUnknown*)_device)->QueryInterface(&dxgiIid, (void**)&dxgiDevice);
            if (hr.Failed)
            {
                return hr.Value;
            }

            // CsWin32 turns a failed HRESULT into an exception carrying it.
            try
            {
                dxgiDevice->SetGPUThreadPriority(priority);
            }
            catch (Exception e)
            {
                hr = new HRESULT(e.HResult);
            }

            try
            {
                int current;
                dxgiDevice->GetGPUThreadPriority(&current);
                readBack = current;
            }
            catch (Exception)
            {
                // The read-back is informational only.
            }

            return hr.Value;
        }
        finally
        {
            if (dxgiDevice is not null) dxgiDevice->Release();
        }
    }

    /// <summary>Creates a BGRA render-target texture, for synthetic sources.</summary>
    public nint CreateTexture(VideoSize size)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture = null;
        _device->CreateTexture2D(&desc, null, &texture);
        return (nint)texture;
    }

    /// <summary>Releases a texture from <see cref="CreateTexture"/>.</summary>
    public static void ReleaseTexture(nint texture)
    {
        if (texture != 0)
        {
            ((IUnknown*)texture)->Release();
        }
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            _context->Release();
            _context = null;
        }

        if (_device is not null)
        {
            WinRTDevice.Dispose();
            _device->Release();
            _device = null;
        }
    }

    private static IDXGIAdapter1* PickAdapter(IDXGIFactory1* factory, HMONITOR monitor)
    {
        IDXGIAdapter1* first = null;
        for (uint i = 0; ; i++)
        {
            IDXGIAdapter1* adapter = null;
            var hr = factory->EnumAdapters1(i, &adapter);
            if (hr == HRESULT.DXGI_ERROR_NOT_FOUND)
            {
                break;
            }

            hr.ThrowOnFailure();
            if (monitor != default && OwnsMonitor(adapter, monitor))
            {
                if (first is not null) first->Release();
                return adapter;
            }

            if (first is null)
            {
                first = adapter;
            }
            else
            {
                adapter->Release();
            }
        }

        return first is not null ? first : throw new InvalidOperationException("No DXGI adapter is present.");
    }

    private static bool OwnsMonitor(IDXGIAdapter1* adapter, HMONITOR monitor)
    {
        for (uint j = 0; ; j++)
        {
            IDXGIOutput* output = null;
            if (adapter->EnumOutputs(j, &output).Failed)
            {
                return false;
            }

            var owns = output->GetDesc().Monitor == monitor;
            output->Release();
            if (owns)
            {
                return true;
            }
        }
    }

    private static IDirect3DDevice CreateWinRTDevice(ID3D11Device* device)
    {
        IDXGIDevice* dxgiDevice = null;
        IInspectable* inspectable = null;
        try
        {
            var dxgiIid = IDXGIDevice.IID_Guid;
            ((IUnknown*)device)->QueryInterface(&dxgiIid, (void**)&dxgiDevice).ThrowOnFailure();
            PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, &inspectable).ThrowOnFailure();
            return MarshalInterface<IDirect3DDevice>.FromAbi((nint)inspectable);
        }
        finally
        {
            if (inspectable is not null) ((IUnknown*)inspectable)->Release();
            if (dxgiDevice is not null) dxgiDevice->Release();
        }
    }
}
