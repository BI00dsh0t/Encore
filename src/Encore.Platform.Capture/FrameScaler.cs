using Microsoft.Extensions.Logging;
using Encore.Media;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Encore.Platform.Capture;

/// <summary>Draws captured frames into the fixed output: fitted, centred, black bars. One thread draws at a time.</summary>
public sealed unsafe partial class FrameScaler : IDisposable
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

    /// <summary>The nv12 flag picks NV12 targets over BGRA; targets is how many target slices to keep output views for.</summary>
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

    /// <summary>Draws the content area of a captured BGRA texture into one slice of the target, letterboxed.</summary>
    public void Draw(nint source, VideoSize contentSize, nint target, uint slice)
    {
        // A simulated reset: the next device call fails, as on a real one.
        _device.ThrowIfSimulatedLoss();
        ((ID3D11Texture2D*)source)->GetDesc(out var sourceDescription);
        var content = contentSize.Intersect(new VideoSize((int)sourceDescription.Width, (int)sourceDescription.Height));
        if (content.Width <= 0 || content.Height <= 0)
        {
            _device.Clear(target, slice, 0f, 0f, 0f);
            return;
        }

        if (!IsNv12 && content == Output)
        {
            var box = FrameCopies.BoxOf(content);
            _device.Context->CopySubresourceRegion((ID3D11Resource*)target, slice, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
            return;
        }

        if (content != _processorContent || _processor is null)
        {
            Rebuild(content);
        }

        var input = InputView(source, sourceDescription);
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
            Textures.Release(_copyIn);
            _copyIn = 0;
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

    /// <summary>A processor for this content size: source rect the content, dest rect its letterbox, black around it.</summary>
    private void Rebuild(VideoSize content)
    {
        // Views belong to the enumerator they were made with.
        _inputs.Clear();
        _outputs.Clear();
        Release(ref _processor);
        Release(ref _enumerator);
        CreateProcessor(content);
        var fit = Letterbox.Fit(content.Width, content.Height, Output.Width, Output.Height);
        ConfigureStream(content, fit);
        _processorContent = content;
        _logger.LogInformation("Scaling {Content} into {Output} at {X},{Y} {Width}x{Height} ({Format})",
            content, Output, fit.X, fit.Y, fit.Width, fit.Height, IsNv12 ? "NV12" : "BGRA");
    }

    private void CreateProcessor(VideoSize content)
    {
        var description = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
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
        _videoDevice->CreateVideoProcessorEnumerator(&description, &enumerator);
        _enumerator = enumerator;

        ID3D11VideoProcessor* processor = null;
        _videoDevice->CreateVideoProcessor(_enumerator, 0, &processor);
        _processor = processor;
    }

    /// <summary>The content drawn into its letterbox, black around it, from full-range RGB to the output's colour space.</summary>
    private void ConfigureStream(VideoSize content, LetterboxRect fit)
    {
        var source = new RECT { left = 0, top = 0, right = content.Width, bottom = content.Height };
        var destination = new RECT { left = fit.X, top = fit.Y, right = fit.X + fit.Width, bottom = fit.Y + fit.Height };
        var whole = new RECT { left = 0, top = 0, right = Output.Width, bottom = Output.Height };
        var black = new D3D11_VIDEO_COLOR();
        black.Anonymous.RGBA = new D3D11_VIDEO_COLOR_RGBA { R = 0f, G = 0f, B = 0f, A = 1f };

        _videoContext->VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        _videoContext->VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext->VideoProcessorSetStreamSourceRect(_processor, 0, true, &source);
        _videoContext->VideoProcessorSetStreamDestRect(_processor, 0, true, &destination);
        _videoContext->VideoProcessorSetOutputTargetRect(_processor, true, &whole);
        _videoContext->VideoProcessorSetOutputBackgroundColor(_processor, false, &black);
        _videoContext->VideoProcessorSetStreamColorSpace1(_processor, 0, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
        _videoContext->VideoProcessorSetOutputColorSpace1(_processor, IsNv12
            ? DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709
            : DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    }

    private ID3D11VideoProcessorInputView* InputView(nint source, D3D11_TEXTURE2D_DESC description)
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

        return CopyInView(source, new VideoSize((int)description.Width, (int)description.Height));
    }

    /// <summary>The fallback: the frame copied into a texture of our own, which always takes a view.</summary>
    private ID3D11VideoProcessorInputView* CopyInView(nint source, VideoSize size)
    {
        if (_copyIn == 0 || _copyInSize != size)
        {
            ReplaceCopyIn(size);
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

    private void ReplaceCopyIn(VideoSize size)
    {
        if (_copyIn != 0)
        {
            _inputs.Remove(_copyIn);
            Textures.Release(_copyIn);
        }

        _copyIn = _device.CreateTexture(size);
        if (_copyIn == 0)
        {
            throw new InvalidOperationException("Could not create the scaler's copy-in texture.");
        }

        _copyInSize = size;
        if (!_copyInLogged)
        {
            _copyInLogged = true;
            _logger.LogWarning("The captured texture can't be a VideoProcessor input; frames are copied in first");
        }
    }

    private ID3D11VideoProcessorInputView* TryCreateInputView(nint texture)
    {
        var description = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
        {
            FourCC = 0,
            ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
        };
        description.Texture2D = new D3D11_TEX2D_VPIV { MipSlice = 0, ArraySlice = 0 };
        ID3D11VideoProcessorInputView* view = null;
        try
        {
            _videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)texture, _enumerator, &description, &view);
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

        ((ID3D11Texture2D*)target)->GetDesc(out var targetDescription);
        var description = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC();
        if (targetDescription.ArraySize > 1)
        {
            description.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
            description.Texture2DArray = new D3D11_TEX2D_ARRAY_VPOV { MipSlice = 0, FirstArraySlice = slice, ArraySize = 1 };
        }
        else
        {
            description.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D;
        }

        ID3D11VideoProcessorOutputView* view = null;
        _videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)target, _enumerator, &description, &view);
        _outputs.Add(target, slice, (nint)view);
        return view;
    }
}
