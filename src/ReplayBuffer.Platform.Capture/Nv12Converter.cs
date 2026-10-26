using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// The X2 colour path: BGRA → NV12 on the D3D11 VideoProcessor of the engine's one device.
/// Captured frames are copied into <see cref="InputTexture"/> (BGRA, the output size) and
/// <see cref="Convert"/> blits it into an NV12 texture (or one slice of an array), as BT.709
/// limited range. The input view is created once; output views are cached per texture and
/// slice, holding a reference on the texture so its address can't be reused while cached.
/// </summary>
public sealed unsafe class Nv12Converter : IDisposable
{
    private readonly D3D11Device _device;
    private readonly nint[] _cachedTextures;
    private readonly uint[] _cachedSlices;
    private readonly nint[] _cachedViews;
    private int _nextEviction;
    private ID3D11VideoDevice* _videoDevice;
    private ID3D11VideoContext1* _videoContext;
    private ID3D11VideoProcessorEnumerator* _enumerator;
    private ID3D11VideoProcessor* _processor;
    private ID3D11VideoProcessorInputView* _inputView;
    private nint _input;

    /// <param name="targets">How many distinct NV12 targets (texture and slice) to keep views for.</param>
    public Nv12Converter(D3D11Device device, VideoSize size, int targets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targets);
        _device = device;
        Size = size;
        _cachedTextures = new nint[targets];
        _cachedSlices = new uint[targets];
        _cachedViews = new nint[targets];
        try
        {
            _input = device.CreateTexture(size);
            if (_input == 0)
            {
                throw new InvalidOperationException("Could not create the BGRA conversion texture.");
            }

            ID3D11VideoDevice* videoDevice = null;
            var videoDeviceIid = ID3D11VideoDevice.IID_Guid;
            ((IUnknown*)device.Device)->QueryInterface(&videoDeviceIid, (void**)&videoDevice).ThrowOnFailure();
            _videoDevice = videoDevice;

            ID3D11VideoContext1* videoContext = null;
            var videoContextIid = ID3D11VideoContext1.IID_Guid;
            ((IUnknown*)device.Context)->QueryInterface(&videoContextIid, (void**)&videoContext).ThrowOnFailure();
            _videoContext = videoContext;

            var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
            {
                InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
                InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
                InputWidth = (uint)size.Width,
                InputHeight = (uint)size.Height,
                OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
                OutputWidth = (uint)size.Width,
                OutputHeight = (uint)size.Height,
                Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
            };
            ID3D11VideoProcessorEnumerator* enumerator = null;
            _videoDevice->CreateVideoProcessorEnumerator(&content, &enumerator);
            _enumerator = enumerator;

            ID3D11VideoProcessor* processor = null;
            _videoDevice->CreateVideoProcessor(_enumerator, 0, &processor);
            _processor = processor;

            var inputDesc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
            {
                FourCC = 0,
                ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
            };
            inputDesc.Texture2D = new D3D11_TEX2D_VPIV { MipSlice = 0, ArraySlice = 0 };
            ID3D11VideoProcessorInputView* inputView = null;
            _videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)_input, _enumerator, &inputDesc, &inputView);
            _inputView = inputView;

            _videoContext->VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
            _videoContext->VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
            _videoContext->VideoProcessorSetStreamColorSpace1(_processor, 0, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
            _videoContext->VideoProcessorSetOutputColorSpace1(_processor, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public VideoSize Size { get; }

    /// <summary>The BGRA texture (render target, the output size) to copy each captured frame into.</summary>
    public nint InputTexture => _input;

    /// <summary>
    /// Converts <see cref="InputTexture"/> into slice <paramref name="slice"/> of the NV12
    /// texture <paramref name="target"/>.
    /// </summary>
    public void Convert(nint target, uint slice)
    {
        var view = OutputView(target, slice);
        var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = true, pInputSurface = _inputView };
        _videoContext->VideoProcessorBlt(_processor, view, 0, 1, &stream);
    }

    private ID3D11VideoProcessorOutputView* OutputView(nint target, uint slice)
    {
        for (var i = 0; i < _cachedViews.Length; i++)
        {
            if (_cachedTextures[i] == target && _cachedSlices[i] == slice && _cachedViews[i] != 0)
            {
                return (ID3D11VideoProcessorOutputView*)_cachedViews[i];
            }
        }

        var free = Array.IndexOf(_cachedViews, (nint)0);
        if (free < 0)
        {
            free = _nextEviction;
            _nextEviction = (_nextEviction + 1) % _cachedViews.Length;
            ReleaseCached(free);
        }

        ((ID3D11Texture2D*)target)->GetDesc(out var desc);
        var outputDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC();
        if (desc.ArraySize > 1)
        {
            outputDesc.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
            outputDesc.Texture2DArray = new D3D11_TEX2D_ARRAY_VPOV { MipSlice = 0, FirstArraySlice = slice, ArraySize = 1 };
        }
        else
        {
            outputDesc.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D;
        }

        ID3D11VideoProcessorOutputView* view = null;
        _videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)target, _enumerator, &outputDesc, &view);
        ((IUnknown*)target)->AddRef();
        _cachedTextures[free] = target;
        _cachedSlices[free] = slice;
        _cachedViews[free] = (nint)view;
        return view;
    }

    public void Dispose()
    {
        ReleaseOutputViews();
        Release(ref _inputView);
        Release(ref _processor);
        Release(ref _enumerator);
        Release(ref _videoContext);
        Release(ref _videoDevice);
        if (_input != 0)
        {
            D3D11Device.ReleaseTexture(_input);
            _input = 0;
        }
    }

    private void ReleaseOutputViews()
    {
        for (var i = 0; i < _cachedViews.Length; i++)
        {
            ReleaseCached(i);
        }
    }

    private void ReleaseCached(int index)
    {
        if (_cachedViews[index] != 0)
        {
            ((IUnknown*)_cachedViews[index])->Release();
            ((IUnknown*)_cachedTextures[index])->Release();
            _cachedViews[index] = 0;
            _cachedTextures[index] = 0;
        }
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
