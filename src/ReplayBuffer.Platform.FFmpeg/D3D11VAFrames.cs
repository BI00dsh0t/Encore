using FFmpeg.AutoGen.Abstractions;
using Windows.Win32.System.Com;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// An FFmpeg D3D11VA device over the engine's own <c>ID3D11Device</c>, and a pool of
/// render-target textures that captured frames are drawn into and NVENC reads from: BGRA
/// (X1 colour path) or NV12 (X2, written by the VideoProcessor). No CPU readback.
/// </summary>
public sealed unsafe class D3D11VAFrames : IDisposable
{
    private const uint BindRenderTarget = 0x20; // D3D11_BIND_RENDER_TARGET

    private AVBufferRef* _device;
    private AVBufferRef* _frames;

    /// <param name="d3d11Device">The engine's <c>ID3D11Device*</c>; FFmpeg takes its own reference.</param>
    /// <param name="poolSize">Textures in the pool; at least NVENC's surfaces + 4.</param>
    /// <param name="nv12">NV12 textures (X2) instead of BGRA (X1).</param>
    public D3D11VAFrames(nint d3d11Device, int width, int height, int poolSize, bool nv12 = false)
    {
        Width = width;
        Height = height;
        IsNv12 = nv12;

        _device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (_device is null)
        {
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(D3D11VA) failed.");
        }

        var deviceContext = (AVHWDeviceContext*)_device->data;
        var d3d11 = (AVD3D11VADeviceContext*)deviceContext->hwctx;

        // FFmpeg releases the device when the context is freed, so it gets its own reference.
        // The immediate context and the lock are filled in by av_hwdevice_ctx_init.
        ((IUnknown*)d3d11Device)->AddRef();
        d3d11->device = (global::FFmpeg.AutoGen.Abstractions.ID3D11Device*)d3d11Device;
        FFmpegLibrary.Check(ffmpeg.av_hwdevice_ctx_init(_device), "av_hwdevice_ctx_init");

        _frames = ffmpeg.av_hwframe_ctx_alloc(_device);
        if (_frames is null)
        {
            throw new InvalidOperationException("av_hwframe_ctx_alloc failed.");
        }

        var framesContext = (AVHWFramesContext*)_frames->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesContext->sw_format = nv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
        framesContext->width = width;
        framesContext->height = height;
        // D3D11 can't make an NV12 texture array a render target (E_INVALIDARG), so the NV12
        // pool is one texture per frame, created on demand and reused by FFmpeg's buffer pool.
        framesContext->initial_pool_size = nv12 ? 0 : poolSize;
        ((AVD3D11VAFramesContext*)framesContext->hwctx)->BindFlags = BindRenderTarget;
        FFmpegLibrary.Check(ffmpeg.av_hwframe_ctx_init(_frames), "av_hwframe_ctx_init");
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsNv12 { get; }

    internal AVBufferRef* FramesRef => _frames;

    /// <summary>Takes a texture from the pool. The frame's data[0] is the texture, data[1] its array slice.</summary>
    internal AVFrame* Acquire()
    {
        var frame = ffmpeg.av_frame_alloc();
        var result = ffmpeg.av_hwframe_get_buffer(_frames, frame, 0);
        if (result < 0)
        {
            ffmpeg.av_frame_free(&frame);
            FFmpegLibrary.Check(result, "av_hwframe_get_buffer");
        }

        return frame;
    }

    public void Dispose()
    {
        if (_frames is not null)
        {
            fixed (AVBufferRef** frames = &_frames)
            {
                ffmpeg.av_buffer_unref(frames);
            }
        }

        if (_device is not null)
        {
            fixed (AVBufferRef** device = &_device)
            {
                ffmpeg.av_buffer_unref(device);
            }
        }
    }
}
