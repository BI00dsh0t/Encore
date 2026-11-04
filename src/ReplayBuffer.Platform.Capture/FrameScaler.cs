using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// Draws captured frames into the fixed output (R20): content fitted inside the output with its
/// aspect ratio kept, centred, black bars (<see cref="Letterbox"/>). Content equal to a BGRA output
/// is a plain <c>CopySubresourceRegion</c>; anything else is one blit on the D3D11 VideoProcessor of
/// the engine's one device, BGRA → BGRA (X1), or BGRA → NV12 BT.709 limited (X2). The processor is
/// rebuilt when the content size changes. Input views are cached per captured texture and output
/// views per target texture and slice; each cached view holds a reference on its texture, so a
/// texture's address can't be reused while it is cached. A captured texture that can't take an
/// input view is copied into an owned texture first, logged once. One thread draws at a time.
/// </summary>
public sealed unsafe class FrameScaler : IDisposable
{
    private const int InputCacheSize = 4;

    private readonly D3D11Device _device;
    private readonly ILogger _logger;
    private readonly ViewCache _inputs = new(InputCacheSize);
    private readonly ViewCache _outputs;
    private ID3D11VideoDevice* _videoDevice;
    private ID3D11VideoContext1* _videoContext;
    private ID3D11VideoProcessorEnumerator* _enumerator;
    private ID3D11VideoProcessor* _processor;
    private VideoSize _processorContent;
    private nint _copyIn;
    private VideoSize _copyInSize;
    private bool _copyInLogged;

    /// <param name="output">The output size every frame is drawn at.</param>
    /// <param name="nv12">Targets are NV12 (X2); otherwise BGRA (X1).</param>
    /// <param name="targets">How many distinct targets (texture and slice) to keep output views for.</param>
    public FrameScaler(D3D11Device device, VideoSize output, bool nv12, int targets, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targets);
        _device = device;
        _logger = logger;
        Output = output;
        IsNv12 = nv12;
        _outputs = new ViewCache(targets);
        try
        {
            ID3D11VideoDevice* videoDevice = null;
            var videoDeviceIid = ID3D11VideoDevice.IID_Guid;
            ((IUnknown*)device.Device)->QueryInterface(&videoDeviceIid, (void**)&videoDevice).ThrowOnFailure();
            _videoDevice = videoDevice;

            ID3D11VideoContext1* videoContext = null;
            var videoContextIid = ID3D11VideoContext1.IID_Guid;
            ((IUnknown*)device.Context)->QueryInterface(&videoContextIid, (void**)&videoContext).ThrowOnFailure();
            _videoContext = videoContext;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public VideoSize Output { get; }

    public bool IsNv12 { get; }

    /// <summary>For tests: never take an input view on the captured texture, always copy it in first.</summary>
    internal bool ForceCopyIn { get; set; }

    /// <summary>
    /// Draws <paramref name="contentSize"/> of the captured BGRA texture <paramref name="source"/>
    /// into slice <paramref name="slice"/> of <paramref name="target"/>, letterboxed.
    /// </summary>
    public void Draw(nint source, VideoSize contentSize, nint target, uint slice)
    {
        // A simulated reset: the next device call fails, as on a real one.
        _device.ThrowIfSimulatedLoss();
        var src = (ID3D11Texture2D*)source;
        src->GetDesc(out var srcDesc);
        var content = contentSize.Intersect(new VideoSize((int)srcDesc.Width, (int)srcDesc.Height));
        if (content.Width <= 0 || content.Height <= 0)
        {
            _device.Clear(target, slice, 0f, 0f, 0f);
            return;
        }

        if (!IsNv12 && content == Output)
        {
            var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = (uint)content.Width, bottom = (uint)content.Height, back = 1 };
            _device.Context->CopySubresourceRegion((ID3D11Resource*)target, slice, 0, 0, 0, (ID3D11Resource*)src, 0, &box);
            return;
        }

        if (content != _processorContent || _processor is null)
        {
            Rebuild(content);
        }

        var input = InputView(source, srcDesc);
        var output = OutputView(target, slice);
        var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = true, pInputSurface = input };
        _videoContext->VideoProcessorBlt(_processor, output, 0, 1, &stream);
    }

    public void Dispose()
    {
        _outputs.Clear();
        _inputs.Clear();
        Release(ref _processor);
        Release(ref _enumerator);
        Release(ref _videoContext);
        Release(ref _videoDevice);
        if (_copyIn != 0)
        {
            D3D11Device.ReleaseTexture(_copyIn);
            _copyIn = 0;
        }
    }

    /// <summary>A processor for this content size: source rect the content, dest rect its letterbox, black around it.</summary>
    private void Rebuild(VideoSize content)
    {
        // Views belong to the enumerator they were made with.
        _inputs.Clear();
        _outputs.Clear();
        Release(ref _processor);
        Release(ref _enumerator);

        var desc = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            InputWidth = (uint)content.Width,
            InputHeight = (uint)content.Height,
            OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            OutputWidth = (uint)Output.Width,
            OutputHeight = (uint)Output.Height,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
        };
        ID3D11VideoProcessorEnumerator* enumerator = null;
        _videoDevice->CreateVideoProcessorEnumerator(&desc, &enumerator);
        _enumerator = enumerator;

        ID3D11VideoProcessor* processor = null;
        _videoDevice->CreateVideoProcessor(_enumerator, 0, &processor);
        _processor = processor;

        var fit = Letterbox.Fit(content.Width, content.Height, Output.Width, Output.Height);
        var source = new RECT { left = 0, top = 0, right = content.Width, bottom = content.Height };
        var dest = new RECT { left = fit.X, top = fit.Y, right = fit.X + fit.Width, bottom = fit.Y + fit.Height };
        var whole = new RECT { left = 0, top = 0, right = Output.Width, bottom = Output.Height };
        var black = new D3D11_VIDEO_COLOR();
        black.Anonymous.RGBA = new D3D11_VIDEO_COLOR_RGBA { R = 0f, G = 0f, B = 0f, A = 1f };

        _videoContext->VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        _videoContext->VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext->VideoProcessorSetStreamSourceRect(_processor, 0, true, &source);
        _videoContext->VideoProcessorSetStreamDestRect(_processor, 0, true, &dest);
        _videoContext->VideoProcessorSetOutputTargetRect(_processor, true, &whole);
        _videoContext->VideoProcessorSetOutputBackgroundColor(_processor, false, &black);
        _videoContext->VideoProcessorSetStreamColorSpace1(_processor, 0, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
        _videoContext->VideoProcessorSetOutputColorSpace1(_processor, IsNv12
            ? DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709
            : DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);

        _processorContent = content;
        _logger.LogInformation("Scaling {Content} into {Output} at {X},{Y} {Width}x{Height} ({Format})",
            content, Output, fit.X, fit.Y, fit.Width, fit.Height, IsNv12 ? "NV12" : "BGRA");
    }

    private ID3D11VideoProcessorInputView* InputView(nint source, D3D11_TEXTURE2D_DESC desc)
    {
        if (!ForceCopyIn && _copyIn == 0)
        {
            if (_inputs.Find(source, 0) is var cached and not 0)
            {
                return (ID3D11VideoProcessorInputView*)cached;
            }

            var view = TryCreateInputView(source);
            if (view is not null)
            {
                _inputs.Add(source, 0, (nint)view);
                return view;
            }
        }

        // The fallback: copy the frame into a texture of our own, which always takes a view.
        var copySize = new VideoSize((int)desc.Width, (int)desc.Height);
        if (_copyIn == 0 || _copyInSize != copySize)
        {
            if (_copyIn != 0)
            {
                _inputs.Remove(_copyIn);
                D3D11Device.ReleaseTexture(_copyIn);
            }

            _copyIn = _device.CreateTexture(copySize);
            if (_copyIn == 0)
            {
                throw new InvalidOperationException("Could not create the scaler's copy-in texture.");
            }

            _copyInSize = copySize;
            if (!_copyInLogged)
            {
                _copyInLogged = true;
                _logger.LogWarning("The captured texture can't be a VideoProcessor input; frames are copied in first");
            }
        }

        _device.Context->CopySubresourceRegion((ID3D11Resource*)_copyIn, 0, 0, 0, 0, (ID3D11Resource*)source, 0, null);
        if (_inputs.Find(_copyIn, 0) is var copyView and not 0)
        {
            return (ID3D11VideoProcessorInputView*)copyView;
        }

        var created = TryCreateInputView(_copyIn);
        if (created is null)
        {
            throw new InvalidOperationException("The scaler's own texture can't be a VideoProcessor input.");
        }

        _inputs.Add(_copyIn, 0, (nint)created);
        return created;
    }

    private ID3D11VideoProcessorInputView* TryCreateInputView(nint texture)
    {
        var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { FourCC = 0, ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D };
        desc.Texture2D = new D3D11_TEX2D_VPIV { MipSlice = 0, ArraySlice = 0 };
        ID3D11VideoProcessorInputView* view = null;
        try
        {
            _videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)texture, _enumerator, &desc, &view);
        }
        catch (Exception e) when (e.HResult < 0)
        {
            return null;
        }

        return view;
    }

    private ID3D11VideoProcessorOutputView* OutputView(nint target, uint slice)
    {
        if (_outputs.Find(target, slice) is var cached and not 0)
        {
            return (ID3D11VideoProcessorOutputView*)cached;
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
        _outputs.Add(target, slice, (nint)view);
        return view;
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

    /// <summary>A fixed set of views keyed by texture and slice, each holding a reference on its texture; full, it evicts round-robin.</summary>
    private sealed class ViewCache(int capacity)
    {
        private readonly nint[] _textures = new nint[capacity];
        private readonly uint[] _slices = new uint[capacity];
        private readonly nint[] _views = new nint[capacity];
        private int _nextEviction;

        public nint Find(nint texture, uint slice)
        {
            for (var i = 0; i < _views.Length; i++)
            {
                if (_views[i] != 0 && _textures[i] == texture && _slices[i] == slice)
                {
                    return _views[i];
                }
            }

            return 0;
        }

        public void Add(nint texture, uint slice, nint view)
        {
            var free = Array.IndexOf(_views, (nint)0);
            if (free < 0)
            {
                free = _nextEviction;
                _nextEviction = (_nextEviction + 1) % _views.Length;
                ReleaseAt(free);
            }

            ((IUnknown*)texture)->AddRef();
            _textures[free] = texture;
            _slices[free] = slice;
            _views[free] = view;
        }

        public void Remove(nint texture)
        {
            for (var i = 0; i < _views.Length; i++)
            {
                if (_textures[i] == texture)
                {
                    ReleaseAt(i);
                }
            }
        }

        public void Clear()
        {
            for (var i = 0; i < _views.Length; i++)
            {
                ReleaseAt(i);
            }
        }

        private void ReleaseAt(int index)
        {
            if (_views[index] != 0)
            {
                ((IUnknown*)_views[index])->Release();
                ((IUnknown*)_textures[index])->Release();
                _views[index] = 0;
                _textures[index] = 0;
            }
        }
    }
}
