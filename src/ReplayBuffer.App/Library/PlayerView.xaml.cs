using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Library;

/// <summary>
/// Draws the player page's frames (AD-27, story 14.2). The player's frame events are coalesced into one present
/// on the UI thread. That present sizes the <see cref="VideoSurface"/> to the picture's pixels, with the video's
/// aspect and never above its own size. It copies the frame in and marks the <see cref="D3DImage"/> dirty. On a
/// resize the old surface stays up, scaled, until the next frame, so a paused picture never blanks. Leaving the
/// page drops its frames before the back buffer goes (the proof's close order).
/// </summary>
internal partial class PlayerView
{
    private readonly D3DImage _image = new();
    private PlayerPage? _page;
    private VideoSurface? _surface;
    private int _presentPending;

    public PlayerView()
    {
        InitializeComponent();
        Picture.Source = _image;
        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        DataContextChanged += (_, e) => Attach(e.NewValue as PlayerPage);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as PlayerPage);
    }

    /// <summary>The surface's size now, or null with none: for tests.</summary>
    internal (int Width, int Height)? SurfaceSize => _surface is { } s ? (s.Width, s.Height) : null;

    /// <summary>Focuses the play button, when the page opens.</summary>
    public void FocusFirstControl() => PlayPause.Focus();

    /// <summary>
    /// The surface size for a <paramref name="videoWidth"/> × <paramref name="videoHeight"/> video shown in
    /// <paramref name="areaWidth"/> × <paramref name="areaHeight"/> pixels: the video's aspect fitted into the
    /// area, never larger than the video, both sides even and at least 2.
    /// </summary>
    internal static (int Width, int Height) Fit(int videoWidth, int videoHeight, double areaWidth, double areaHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return (2, 2);
        }

        var scale = Math.Min(1, Math.Min(Math.Max(areaWidth, 2) / videoWidth, Math.Max(areaHeight, 2) / videoHeight));
        static int Even(double value) => Math.Max(2, (int)value & ~1);
        return (Even(videoWidth * scale), Even(videoHeight * scale));
    }

    private void Attach(PlayerPage? page)
    {
        if (ReferenceEquals(page, _page))
        {
            return;
        }

        if (_page is { } old)
        {
            old.Player.FrameAvailable -= OnFrameAvailable;
            old.PropertyChanged -= OnPagePropertyChanged;
        }

        _page = page;
        ReleaseSurface();
        if (page is not null && IsLoaded)
        {
            page.Player.FrameAvailable += OnFrameAvailable;
            page.PropertyChanged += OnPagePropertyChanged;
        }
        else
        {
            _page = null;
        }
    }

    // Any thread: at most one present waits on the UI thread at a time.
    private void OnFrameAvailable()
    {
        if (Interlocked.Exchange(ref _presentPending, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, Present);
        }
    }

    private void Present()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        if (_page is not { IsClosed: false, Media: { } media } page || !_image.IsFrontBufferAvailable)
        {
            return;
        }

        try
        {
            if (EnsureSurface(media) is not { } surface)
            {
                return;
            }

            page.Player.CopyFrame(surface);
            surface.Flush();
            _image.Lock();
            _image.AddDirtyRect(new Int32Rect(0, 0, surface.Width, surface.Height));
            _image.Unlock();
        }
#pragma warning disable CA1031 // A frame that can't be copied (a lost device, a closing player) is skipped, never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // The surface at the size the picture wants now, made or remade; null when there's no window or GPU for it.
    private VideoSurface? EnsureSurface(PlayerMedia media)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = (FrameworkElement)Picture.Parent;
        var (width, height) = Fit(media.Width, media.Height, area.ActualWidth * dpi.DpiScaleX, area.ActualHeight * dpi.DpiScaleY);
        if (_surface is { } current && current.Width == width && current.Height == height)
        {
            return current;
        }

        if (PresentationSource.FromVisual(this) is not HwndSource window)
        {
            return null;
        }

        var next = new VideoSurface(width, height, window.Handle);
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, next.BackBuffer, enableSoftwareFallback: true);
        _image.Unlock();
        _surface?.Dispose();
        _surface = next;
        return next;
    }

    private void ReleaseSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        _image.Unlock();
        _surface.Dispose();
        _surface = null;
    }

    // Once the page has its media, the first frame (already shown, paused) is asked for again for this view.
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPage.Media))
        {
            OnFrameAvailable();
        }
    }

    // A lock screen or display change loses the D3D9 device: rebuild the surface when it's back, and ask for a frame.
    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || _page is not { IsClosed: false } page)
        {
            return;
        }

        ReleaseSurface();
        if (!page.IsPlaying && page.Player.Position == TimeSpan.Zero)
        {
            page.Player.ShowFirstFrame();
        }
    }
}
