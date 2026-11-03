using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace ReplayBuffer.Platform.Desktop;

/// <summary>A thumbnail's pixels: 32-bpp premultiplied BGRA, as the shell returns them, top-down, <c>Width * 4</c> bytes a row.</summary>
public sealed record ShellImage(int Width, int Height, byte[] Bgra);

/// <summary>
/// A file's thumbnail from the Windows shell (story 7.3): the App passes only the path, and the shell
/// reads the file (AD-1). Call it on an STA thread, and never on the UI thread: extraction can be slow.
/// </summary>
public static unsafe class ShellThumbnail
{
    /// <summary>
    /// The shell's thumbnail of <paramref name="path"/>, at most <paramref name="size"/> pixels square
    /// (<c>SIIGBF_THUMBNAILONLY</c>, so never the file type's icon); null when the shell has none.
    /// </summary>
    public static ShellImage? TryGet(string path, int size)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        IShellItemImageFactory* factory = null;
        var bitmap = HBITMAP.Null;
        try
        {
            var iid = IShellItemImageFactory.IID_Guid;
            void* created;
            fixed (char* name = path)
            {
                if (PInvoke.SHCreateItemFromParsingName(name, null, &iid, &created).Failed)
                {
                    return null;
                }
            }

            factory = (IShellItemImageFactory*)created;

            // The vtable call, not the wrapper, so a failing HRESULT is a null rather than an exception.
            var vtable = *(void***)factory;
            var getImage = (delegate* unmanaged[Stdcall]<IShellItemImageFactory*, SIZE, SIIGBF, HBITMAP*, HRESULT>)vtable[3];
            if (getImage(factory, new SIZE(size, size), SIIGBF.SIIGBF_THUMBNAILONLY, &bitmap).Failed || bitmap.IsNull)
            {
                return null;
            }

            return Copy(bitmap);
        }
        finally
        {
            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(bitmap.Value));
            }

            if (factory is not null)
            {
                factory->Release();
            }
        }
    }

    // The DIB section's 32-bpp pixels, top-down; an image with no alpha at all is made opaque.
    private static ShellImage? Copy(HBITMAP bitmap)
    {
        DIBSECTION dib;
        if (PInvoke.GetObject(new HGDIOBJ(bitmap.Value), sizeof(DIBSECTION), &dib) != sizeof(DIBSECTION))
        {
            return null;
        }

        var bm = dib.dsBm;
        if (bm.bmBits is null || bm.bmBitsPixel != 32 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            return null;
        }

        var width = bm.bmWidth;
        var height = bm.bmHeight;
        var row = width * 4;
        var pixels = new byte[row * height];
        var bottomUp = dib.dsBmih.biHeight > 0;
        for (var y = 0; y < height; y++)
        {
            var source = new ReadOnlySpan<byte>((byte*)bm.bmBits + ((long)(bottomUp ? height - 1 - y : y) * bm.bmWidthBytes), row);
            source.CopyTo(pixels.AsSpan(y * row, row));
        }

        var noAlpha = true;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                noAlpha = false;
                break;
            }
        }

        if (noAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        return new ShellImage(width, height, pixels);
    }
}
