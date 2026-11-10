using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;
using IWinRTObject = WinRT.IWinRTObject;
using MarshalInterface = WinRT.MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>;

namespace Encore.Platform.Desktop;

/// <summary>
/// The in-app player's picture (AD-27, story 14.2): one BGRA texture with two views. Windows' media player
/// copies each frame into it through <see cref="Direct3DSurface"/>, and a WPF <c>D3DImage</c> shows it through
/// <see cref="BackBuffer"/>, so the page's controls can draw over the video.
/// <para>
/// It owns a D3D11 device and a D3D9Ex device on the App's window, made once. <see cref="Resize"/> remakes
/// only the texture and its views. Every step checks its HRESULT and throws on failure (CsWin32's methods
/// call <c>ThrowOnFailure</c>), so a caller sees a GPU failure rather than a blank picture.
/// <see cref="Dispose"/> releases every COM reference it or the WinRT wrapper took, at once, with no GC.
/// Create, use and dispose it on the UI thread.
/// </para>
/// </summary>
public sealed unsafe class VideoSurface : IDisposable
{
    private const uint PresentIntervalImmediate = 0x80000000;

    private readonly HWND _window;
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private IDirect3D9Ex* _d3d9;
    private IDirect3DDevice9Ex* _device9;
    private ID3D11Texture2D* _texture;
    private IDirect3DTexture9* _texture9;
    private IDirect3DSurface9* _surface9;
    private IInspectable* _winrtSurface;
    private IDirect3DSurface? _direct3DSurface;

    /// <summary>Creates both devices, with no texture yet; throws when the GPU refuses.</summary>
    /// <param name="window">The window the D3D9Ex device belongs to: the App window that shows the picture.</param>
    public VideoSurface(nint window)
    {
        _window = new HWND(window);
        try
        {
            CreateDevices();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Creates both devices and a <paramref name="width"/> × <paramref name="height"/> texture.</summary>
    public VideoSurface(int width, int height, nint window)
        : this(window) => Resize(width, height);

    /// <summary>The texture's width in pixels; 0 before the first <see cref="Resize"/>.</summary>
    public int Width { get; private set; }

    /// <summary>The texture's height in pixels; 0 before the first <see cref="Resize"/>.</summary>
    public int Height { get; private set; }

    /// <summary>The texture's D3D9 surface (an <c>IDirect3DSurface9*</c>), for <c>D3DImage.SetBackBuffer</c>; zero with no texture.</summary>
    public nint BackBuffer => (nint)_surface9;

    /// <summary>The texture as the WinRT surface that <c>MediaPlayer.CopyFrameToVideoSurface</c> writes into.</summary>
    public IDirect3DSurface Direct3DSurface
    {
        get
        {
            ObjectDisposedException.ThrowIf(_winrtSurface is null, this);
            return _direct3DSurface ??= MarshalInterface.FromAbi((nint)_winrtSurface);
        }
    }

    /// <summary>
    /// Remakes the texture and its views at <paramref name="width"/> × <paramref name="height"/>, on the same
    /// devices; a no-op at the current size. The old views are released first, so a caller must take the new
    /// <see cref="BackBuffer"/> and <see cref="Direct3DSurface"/>.
    /// </summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ObjectDisposedException.ThrowIf(_device is null, this);
        if (_texture is not null && width == Width && height == Height)
        {
            return;
        }

        ReleaseTexture();
        try
        {
            CreateTexture(width, height);
            CreateD3D9View(SharedHandle(), width, height);
            CreateWinRTView();
            (Width, Height) = (width, height);
        }
        catch
        {
            ReleaseTexture();
            throw;
        }
    }

    /// <summary>Submits the copy just made, before WPF reads the D3D9 view.</summary>
    public void Flush()
    {
        if (_context is not null)
        {
            _context->Flush();
        }
    }

    /// <summary>Releases the texture, its views and both devices; safe to call twice.</summary>
    public void Dispose()
    {
        ReleaseTexture();
        Release(ref _device9);
        Release(ref _d3d9);
        Release(ref _context);
        Release(ref _device);
    }

    /// <summary>Tests only: adds a reference to every COM object it holds, in the order <see cref="ReleaseForTest"/> should release them.</summary>
    internal nint[] AddRefsForTest()
    {
        // The D3D9 surface level shares its count with its texture, so the texture stands for both.
        var all = new nint[] { (nint)_winrtSurface, (nint)_texture9, (nint)_texture, (nint)_device9, (nint)_d3d9, (nint)_context, (nint)_device };
        foreach (var pointer in all)
        {
            ((IUnknown*)pointer)->AddRef();
        }

        return all;
    }

    /// <summary>Tests only: releases a reference <see cref="AddRefsForTest"/> took, returning how many are left.</summary>
    internal static uint ReleaseForTest(nint pointer) => ((IUnknown*)pointer)->Release();

    // A hardware D3D11 device (the texture's) and a D3D9Ex device on the window (D3DImage's).
    private void CreateDevices()
    {
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0],
            PInvoke.D3D11_SDK_VERSION,
            &device,
            out _,
            &context).ThrowOnFailure();
        _device = device;
        _context = context;

        IDirect3D9Ex* d3d9;
        PInvoke.Direct3DCreate9Ex(PInvoke.D3D_SDK_VERSION, &d3d9).ThrowOnFailure();
        _d3d9 = d3d9;
        var present = new D3DPRESENT_PARAMETERS
        {
            Windowed = true,
            SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
            hDeviceWindow = _window,
            PresentationInterval = PresentIntervalImmediate,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
        };
        IDirect3DDevice9Ex* device9;
        _d3d9->CreateDeviceEx(
            0,
            D3DDEVTYPE.D3DDEVTYPE_HAL,
            _window,
            PInvoke.D3DCREATE_HARDWARE_VERTEXPROCESSING | PInvoke.D3DCREATE_MULTITHREADED | PInvoke.D3DCREATE_FPU_PRESERVE,
            &present,
            null,
            &device9);
        _device9 = device9;
    }

    // The shared BGRA texture the frame server renders into.
    private void CreateTexture(int width, int height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED,
        };
        ID3D11Texture2D* texture;
        _device->CreateTexture2D(&desc, null, &texture);
        _texture = texture;
    }

    private HANDLE SharedHandle()
    {
        IDXGIResource* resource;
        var iid = IDXGIResource.IID_Guid;
        ((IUnknown*)_texture)->QueryInterface(&iid, (void**)&resource).ThrowOnFailure();
        try
        {
            HANDLE shared;
            resource->GetSharedHandle(&shared);
            return shared;
        }
        finally
        {
            resource->Release();
        }
    }

    // The D3D9 texture opened on the shared handle, and its surface: what D3DImage shows.
    private void CreateD3D9View(HANDLE shared, int width, int height)
    {
        IDirect3DTexture9* texture9;
        _device9->CreateTexture((uint)width, (uint)height, 1, PInvoke.D3DUSAGE_RENDERTARGET, D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_DEFAULT, &texture9, &shared);
        _texture9 = texture9;
        IDirect3DSurface9* surface9;
        _texture9->GetSurfaceLevel(0, &surface9);
        _surface9 = surface9;
    }

    // The WinRT IDirect3DSurface over the texture's DXGI surface (Windows.Graphics.DirectX.Direct3D11 interop).
    private void CreateWinRTView()
    {
        IDXGISurface* dxgiSurface;
        var iid = IDXGISurface.IID_Guid;
        ((IUnknown*)_texture)->QueryInterface(&iid, (void**)&dxgiSurface).ThrowOnFailure();
        try
        {
            IInspectable* winrt;
            PInvoke.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, &winrt).ThrowOnFailure();
            _winrtSurface = winrt;
        }
        finally
        {
            dxgiSurface->Release();
        }
    }

    // The texture and its three views. The WinRT wrapper holds its own references (its native reference and
    // one per interface it was used through) until a GC, even after Close: each is disposed here, so the
    // texture and both devices go at once (QA 14.2 round 1).
    private void ReleaseTexture()
    {
        if (_direct3DSurface is { } wrapper)
        {
            _direct3DSurface = null;
            var native = (IWinRTObject)wrapper;
            wrapper.Dispose();
            native.GetObjectReferenceForType(typeof(IDirect3DSurface).TypeHandle).Dispose();
            native.GetObjectReferenceForType(typeof(IDisposable).TypeHandle).Dispose();
            native.NativeObject.Dispose();
        }

        Release(ref _winrtSurface);
        Release(ref _surface9);
        Release(ref _texture9);
        Release(ref _texture);
        (Width, Height) = (0, 0);
    }

    private static void Release<T>(ref T* pointer)
        where T : unmanaged
    {
        if (pointer is not null)
        {
            ((IUnknown*)pointer)->Release();
            pointer = null;
        }
    }
}
