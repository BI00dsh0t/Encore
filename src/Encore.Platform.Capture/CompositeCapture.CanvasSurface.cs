using Microsoft.Extensions.Logging;
using Encore.Media;

namespace Encore.Platform.Capture;

public sealed partial class CompositeCapture
{
    /// <summary>The canvas a <see cref="CompositeCapture"/> draws into: the GPU texture, or a test's fake.</summary>
    internal interface ICanvasTarget : ICanvasSurface, IDisposable;

    /// <summary>The canvas texture: a frame is copied into its screen's place, and scaled first when its size has changed.</summary>
    private sealed class CanvasSurface : ICanvasTarget
    {
        private readonly CompositeCapture _owner;
        private readonly VideoSize _size;
        private readonly Dictionary<CanvasRect, (FrameScaler Scaler, nint Staging)> _scaled = [];
        private readonly D3D11Device _device;
        private readonly Lock _clearing = new();
        private nint _canvas;

        public CanvasSurface(CompositeCapture owner, VideoSize size)
        {
            _owner = owner;
            _device = owner._device!;
            _size = size;
            _canvas = _device.CreateTexture(size);
            if (_canvas == 0)
            {
                throw new InvalidOperationException($"Could not create the {size} canvas texture.");
            }

            _device.Clear(_canvas, 0, 0f, 0f, 0f);
        }

        public void Blit(nint texture, int contentWidth, int contentHeight, CanvasRect area)
        {
            var place = new VideoSize(area.Width, area.Height);
            var content = new VideoSize(contentWidth, contentHeight);
            if (content == place)
            {
                _device.CopyFrameTo(texture, content, _canvas, area.X, area.Y, place);
                return;
            }

            var scaled = ScalerFor(area, place, content);
            scaled.Scaler.Draw(texture, content, scaled.Staging, 0);
            _device.CopyFrameTo(scaled.Staging, place, _canvas, area.X, area.Y, place);
        }

        public void Clear(CanvasRect area)
        {
            // A removal (the orchestrator's thread) may race a teardown: once the canvas is released, nothing to clear.
            lock (_clearing)
            {
                if (_canvas == 0)
                {
                    return;
                }

                // A black frame of the place's size, copied over it: Clear fills a whole texture.
                var place = new VideoSize(area.Width, area.Height);
                var black = _device.CreateTexture(place);
                try
                {
                    _device.Clear(black, 0, 0f, 0f, 0f);
                    _device.CopyFrameTo(black, place, _canvas, area.X, area.Y, place);
                }
                finally
                {
                    D3D11Device.ReleaseTexture(black);
                }
            }
        }

        public void Present(long qpc) => _owner.Handler(qpc, _canvas, _size);

        public void Dispose()
        {
            foreach (var (scaler, staging) in _scaled.Values)
            {
                scaler.Dispose();
                D3D11Device.ReleaseTexture(staging);
            }

            _scaled.Clear();
            lock (_clearing)
            {
                D3D11Device.ReleaseTexture(_canvas);
                _canvas = 0;
            }
        }

        // A screen whose frames don't match its place is scaled into a staging texture of the place's size first.
        private (FrameScaler Scaler, nint Staging) ScalerFor(CanvasRect area, VideoSize place, VideoSize content)
        {
            if (_scaled.TryGetValue(area, out var scaled))
            {
                return scaled;
            }

            var staging = _device.CreateTexture(place);
            scaled = (new FrameScaler(_device, place, nv12: false, targets: 1, _owner.Logger), staging);
            _scaled[area] = scaled;
            _owner.Logger.LogInformation(
                "A screen at ({X},{Y}) delivers {Content} into its {Place} place: scaled from now on", area.X, area.Y, content, place);
            return scaled;
        }
    }
}
