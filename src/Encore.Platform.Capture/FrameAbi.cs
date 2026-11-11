using Windows.Graphics.Capture;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT.Direct3D11;
using WinRT;

namespace Encore.Platform.Capture;

/// <summary>WGC's frame pool and frame calls made through their ABI vtables, so no frame allocates a managed wrapper.</summary>
internal static unsafe class FrameAbi
{
    // IDirect3DDxgiInterfaceAccess, which hands out a surface's D3D11 texture.
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // The per-frame ABI interfaces and their vtable slots, which come after IInspectable's six.
    private static readonly Guid FramePoolIid = new("24EB6D22-1975-422E-82E7-780DBD8DDF24");
    private static readonly Guid ClosableIid = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private const int TryGetNextFrameSlot = 7; // IDirect3D11CaptureFramePool: Recreate, TryGetNextFrame, ...
    private const int SurfaceSlot = 6;         // IDirect3D11CaptureFrame: get_Surface, get_SystemRelativeTime, get_ContentSize
    private const int SystemRelativeTimeSlot = 7;
    private const int ContentSizeSlot = 8;
    private const int CloseSlot = 6;           // IClosable: Close

    /// <summary>The pool's <c>IDirect3D11CaptureFramePool</c> ABI pointer, owned by the caller.</summary>
    public static void* QueryPool(Direct3D11CaptureFramePool pool)
    {
        var unknown = (void*)MarshalInspectable<object>.FromManaged(pool);
        try
        {
            void* abi = null;
            var iid = FramePoolIid;
            ((IUnknown*)unknown)->QueryInterface(&iid, &abi).ThrowOnFailure();
            return abi;
        }
        finally
        {
            ((IUnknown*)unknown)->Release();
        }
    }

    /// <summary>The pool's next frame, which the caller closes with <see cref="Close"/>; null when there is none.</summary>
    public static void* TryGetNextFrame(void* pool)
    {
        void* frame = null;
        var next = (delegate* unmanaged[Stdcall]<void*, void**, HRESULT>)(*(void***)pool)[TryGetNextFrameSlot];
        return next(pool, &frame).Failed ? null : frame;
    }

    /// <summary>Reads a frame's content size, its time in 100 ns and its surface, which the caller releases.</summary>
    public static void Read(void* frame, out VideoSize content, out long relativeTime, out void* surface)
    {
        var vtable = *(void***)frame;
        AbiSize size;
        long time;
        void* read = null;
        ((delegate* unmanaged[Stdcall]<void*, AbiSize*, HRESULT>)vtable[ContentSizeSlot])(frame, &size).ThrowOnFailure();
        ((delegate* unmanaged[Stdcall]<void*, long*, HRESULT>)vtable[SystemRelativeTimeSlot])(frame, &time).ThrowOnFailure();
        ((delegate* unmanaged[Stdcall]<void*, void**, HRESULT>)vtable[SurfaceSlot])(frame, &read).ThrowOnFailure();
        content = new VideoSize(size.Width, size.Height);
        relativeTime = time;
        surface = read;
    }

    /// <summary>Closes a frame (handing its buffer back to the pool) and releases it.</summary>
    public static void Close(void* frame)
    {
        void* closable = null;
        var closableIid = ClosableIid;
        if (((IUnknown*)frame)->QueryInterface(&closableIid, &closable).Succeeded)
        {
            _ = ((delegate* unmanaged[Stdcall]<void*, HRESULT>)(*(void***)closable)[CloseSlot])(closable);
            ((IUnknown*)closable)->Release();
        }

        ((IUnknown*)frame)->Release();
    }

    /// <summary>The surface's <c>ID3D11Texture2D</c>; releases <paramref name="surface"/>, the caller releases the texture.</summary>
    public static ID3D11Texture2D* TextureOf(void* surface)
    {
        void* access = null;
        try
        {
            var accessIid = DxgiInterfaceAccessIid;
            ((IUnknown*)surface)->QueryInterface(&accessIid, &access).ThrowOnFailure();

            void* texture = null;
            var textureIid = ID3D11Texture2D.IID_Guid;
            ((IDirect3DDxgiInterfaceAccess*)access)->GetInterface(&textureIid, &texture);
            return (ID3D11Texture2D*)texture;
        }
        finally
        {
            if (access is not null) ((IUnknown*)access)->Release();
            ((IUnknown*)surface)->Release();
        }
    }

    /// <summary>The ABI <c>SizeInt32</c>.</summary>
    private struct AbiSize
    {
        public int Width;
        public int Height;
    }
}
