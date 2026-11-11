using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Encore.Platform.Capture;

/// <summary>The engine's one D3D11 device, multithread-protected because WGC, the engine and NVENC share its context.</summary>
public sealed unsafe partial class D3D11Device : IDisposable
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

    /// <summary>Why the device was removed: 0 while it works, a failed HRESULT once lost; a simulated loss reads as a reset.</summary>
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

    /// <summary>Test seam: this device reports itself lost from now on, and its next draw fails as a reset device's would.</summary>
    public void SimulateLoss() => _simulatedLoss = true;

    /// <summary>Test seam for every device: each one created before this call reports itself lost, as after a driver reset.</summary>
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

    /// <summary>Queries the device's <c>IDXGIDevice</c>, which the caller releases, and returns the query's result.</summary>
    internal HRESULT QueryDxgiDevice(IDXGIDevice** dxgiDevice) => QueryDxgiDevice(_device, dxgiDevice);

    /// <summary>The WinRT wrapper WGC frame pools are created on.</summary>
    public IDirect3DDevice WinRTDevice { get; }

    public string AdapterName { get; }

    /// <summary>Submits the queued GPU work, as an encode of the copied frame would.</summary>
    public void Flush() => _context->Flush();

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

    private static HRESULT QueryDxgiDevice(ID3D11Device* device, IDXGIDevice** dxgiDevice)
    {
        var dxgiIid = IDXGIDevice.IID_Guid;
        return ((IUnknown*)device)->QueryInterface(&dxgiIid, (void**)dxgiDevice);
    }
}
