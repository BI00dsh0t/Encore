using Windows.Win32.Graphics.Direct3D11;

namespace Encore.Platform.Capture;

/// <summary>Draws captured frames into target textures on a device: copies and clears, never scales.</summary>
public static unsafe class FrameCopies
{
    /// <summary>Copies a captured frame into one slice of the target; a size mismatch clears to black and clips, never scales.</summary>
    public static void CopyFrame(
        this D3D11Device device, nint source, VideoSize contentSize, nint target, uint targetSubresource, VideoSize targetSize)
    {
        var copy = ClippedCopy(source, contentSize, targetSize);
        if (copy != targetSize)
        {
            device.Clear(target, targetSubresource, 0f, 0f, 0f);
        }

        var box = BoxOf(copy);
        device.Context->CopySubresourceRegion((ID3D11Resource*)target, targetSubresource, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
    }

    /// <summary>Copies a captured frame to a position inside the target, clipped to the monitor's area; never scales.</summary>
    public static void CopyFrameTo(this D3D11Device device, nint source, VideoSize contentSize, nint target, int x, int y, VideoSize area)
    {
        var box = BoxOf(ClippedCopy(source, contentSize, area));
        device.Context->CopySubresourceRegion((ID3D11Resource*)target, 0, (uint)x, (uint)y, 0, (ID3D11Resource*)source, 0, &box);
    }

    /// <summary>Fills slice <paramref name="subresource"/> of a render-target texture with one colour.</summary>
    public static void Clear(this D3D11Device device, nint target, uint subresource, float red, float green, float blue)
    {
        var texture = (ID3D11Texture2D*)target;
        texture->GetDesc(out var description);

        var view = new D3D11_RENDER_TARGET_VIEW_DESC { Format = description.Format };
        if (description.ArraySize > 1)
        {
            view.ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
            view.Anonymous.Texture2DArray = new D3D11_TEX2D_ARRAY_RTV { MipSlice = 0, FirstArraySlice = subresource, ArraySize = 1 };
        }
        else
        {
            view.ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D;
        }

        ID3D11RenderTargetView* targetView = null;
        device.Device->CreateRenderTargetView((ID3D11Resource*)texture, &view, &targetView);
        try
        {
            var color = stackalloc float[4] { red, green, blue, 1f };
            device.Context->ClearRenderTargetView(targetView, color);
        }
        finally
        {
            targetView->Release();
        }
    }

    /// <summary>The top-left region of a texture of this size, as a copy's source box.</summary>
    internal static D3D11_BOX BoxOf(VideoSize size) =>
        new() { left = 0, top = 0, front = 0, right = (uint)size.Width, bottom = (uint)size.Height, back = 1 };

    /// <summary>What of a frame can be copied: its content, inside the area and the texture's own size.</summary>
    private static VideoSize ClippedCopy(nint source, VideoSize contentSize, VideoSize area)
    {
        ((ID3D11Texture2D*)source)->GetDesc(out var description);
        return contentSize.Intersect(area).Intersect(new VideoSize((int)description.Width, (int)description.Height));
    }
}
