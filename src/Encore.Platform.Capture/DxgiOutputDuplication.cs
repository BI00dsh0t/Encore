using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;

namespace Encore.Platform.Capture;

/// <summary>DXGI's <c>IDXGIOutputDuplication</c> of the device adapter's output that shows a monitor.</summary>
internal sealed unsafe class DxgiOutputDuplication(D3D11Device device, nint monitor) : IOutputDuplication
{
    public static readonly HRESULT DeviceRemoved = new(unchecked((int)0x887A0005)); // DXGI_ERROR_DEVICE_REMOVED
    public static readonly HRESULT DeviceReset = new(unchecked((int)0x887A0007));   // DXGI_ERROR_DEVICE_RESET

    // The raw vtable slots, for their HRESULTs: CsWin32's struct methods throw on failure.
    private const int GetAdapterSlot = 7;        // IDXGIDevice: IDXGIObject's four, then GetAdapter
    private const int DuplicateOutputSlot = 22;  // IDXGIOutput1
    private const int AcquireNextFrameSlot = 8;  // IDXGIOutputDuplication
    private const int ReleaseFrameSlot = 14;

    private readonly HMONITOR _monitor = new((void*)monitor);
    private IDXGIOutputDuplication* _duplication;
    private IDXGIResource* _resource;
    private ID3D11Texture2D* _texture;

    public VideoSize ModeSize
    {
        get
        {
            _duplication->GetDesc(out var description);
            return new VideoSize((int)description.ModeDesc.Width, (int)description.ModeDesc.Height);
        }
    }

    public HRESULT TryAcquire(out DuplicatedFrame frame)
    {
        frame = default;
        DXGI_OUTDUPL_FRAME_INFO info;
        IDXGIResource* resource = null;
        var duplication = _duplication;

        // A zero timeout: while AcquireNextFrame waits it holds the device lock the encoder needs.
        var result = ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, uint, DXGI_OUTDUPL_FRAME_INFO*, IDXGIResource**, HRESULT>)
            (*(void***)duplication)[AcquireNextFrameSlot])(duplication, 0, &info, &resource);
        if (result.Failed)
        {
            return result;
        }

        _resource = resource;
        if (resource is null)
        {
            frame = new DuplicatedFrame(info.LastPresentTime, 0, default);
            return result;
        }

        ID3D11Texture2D* texture = null;
        var textureIid = ID3D11Texture2D.IID_Guid;
        ((IUnknown*)resource)->QueryInterface(&textureIid, (void**)&texture).ThrowOnFailure();
        _texture = texture;
        texture->GetDesc(out var description);
        frame = new DuplicatedFrame(
            info.LastPresentTime, (nint)texture, new VideoSize((int)description.Width, (int)description.Height));
        return result;
    }

    public HRESULT ReleaseFrame()
    {
        ReleaseResource();
        var duplication = _duplication;
        return ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, HRESULT>)(*(void***)duplication)[ReleaseFrameSlot])(duplication);
    }

    public void Release()
    {
        ReleaseResource();
        if (_duplication is not null)
        {
            _duplication->Release();
            _duplication = null;
        }
    }

    /// <summary>Duplicates the device adapter's output that shows the monitor.</summary>
    public HRESULT Duplicate()
    {
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIOutput1* output = null;
        try
        {
            var result = device.QueryDxgiDevice(&dxgiDevice);
            if (result.Failed)
            {
                return result;
            }

            result = ((delegate* unmanaged[Stdcall]<IDXGIDevice*, IDXGIAdapter**, HRESULT>)
                (*(void***)dxgiDevice)[GetAdapterSlot])(dxgiDevice, &adapter);
            if (result.Failed)
            {
                return result;
            }

            result = FindOutput(adapter, &output);
            if (result.Failed)
            {
                return result;
            }

            IDXGIOutputDuplication* created = null;
            result = ((delegate* unmanaged[Stdcall]<IDXGIOutput1*, IUnknown*, IDXGIOutputDuplication**, HRESULT>)
                (*(void***)output)[DuplicateOutputSlot])(output, (IUnknown*)device.Device, &created);
            if (result.Succeeded)
            {
                _duplication = created;
            }

            return result;
        }
        finally
        {
            if (output is not null) output->Release();
            if (adapter is not null) adapter->Release();
            if (dxgiDevice is not null) dxgiDevice->Release();
        }
    }

    /// <summary>The adapter's output that shows the monitor, as <c>IDXGIOutput1</c>; the enumeration's failure when none does.</summary>
    private HRESULT FindOutput(IDXGIAdapter* adapter, IDXGIOutput1** output)
    {
        for (uint index = 0; ; index++)
        {
            IDXGIOutput* candidate = null;
            var result = adapter->EnumOutputs(index, &candidate);
            if (result.Failed)
            {
                return result;
            }

            if (candidate->GetDesc().Monitor != _monitor)
            {
                candidate->Release();
                continue;
            }

            var output1Iid = IDXGIOutput1.IID_Guid;
            result = ((IUnknown*)candidate)->QueryInterface(&output1Iid, (void**)output);
            candidate->Release();
            return result;
        }
    }

    private void ReleaseResource()
    {
        if (_texture is not null)
        {
            _texture->Release();
            _texture = null;
        }

        if (_resource is not null)
        {
            _resource->Release();
            _resource = null;
        }
    }
}
