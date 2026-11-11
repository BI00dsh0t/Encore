using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Encore.Platform.Capture;

/// <summary>Makes and frees the BGRA render-target textures that synthetic sources and staging copies draw into.</summary>
public static unsafe class Textures
{
    /// <summary>Creates a BGRA render-target texture, for synthetic sources.</summary>
    public static nint CreateTexture(this D3D11Device device, VideoSize size)
    {
        var description = new D3D11_TEXTURE2D_DESC
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
        device.Device->CreateTexture2D(&description, null, &texture);
        return (nint)texture;
    }

    /// <summary>Releases a texture from <see cref="CreateTexture"/>.</summary>
    public static void Release(nint texture)
    {
        if (texture != 0)
        {
            ((IUnknown*)texture)->Release();
        }
    }
}
