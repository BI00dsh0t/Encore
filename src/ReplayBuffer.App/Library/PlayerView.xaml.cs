using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Library;

/// <summary>
/// Draws the player page's frames (AD-27, story 14.2).
/// <list type="bullet">
/// <item>The player's frame events are coalesced into one present on the UI thread. It copies the frame
/// into the <see cref="VideoSurface"/> and marks the <see cref="D3DImage"/> dirty.</item>
/// <item>The surface's two devices are made once per page. Its texture is sized to the picture's pixels,
/// with the video's aspect and never above the video's own size. It's remade only after a resize settles,
/// and until then the old one is drawn scaled, so a paused picture never blanks.</item>
/// <item>A GPU failure building the surface, or no picture within 5 s of opening (story 14.6), puts the page in its
/// Failed state rather than leaving a blank picture.</item>
/// <item>After a lost front buffer (a lock screen, a display change) the surface is remade and the current frame redrawn.</item>
/// <item>Leaving the page drops its frames before the back buffer goes (the proof's close order).</item>
/// <item>The transport (story 14.5): the position is read for the seek bar about ten times a second while playing,
/// a drag of the seek bar's thumb seeks about once a rendered frame, and the keys bubble here, so a focused
/// control's own keys win.</item>
/// </list>
/// </summary>
internal partial class PlayerView
{
    // How long the picture's size must hold before the texture is remade at it.
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the page waits for its first picture before saying the file can't be played (story 14.6); tests shorten it.</summary>
    internal static TimeSpan NoPictureTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // How often the seek bar and time follow the player while it plays.
    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(100);

    private readonly D3DImage _image = new();
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _position;
    private readonly DispatcherTimer _noPicture;
    private PlayerPage? _page;
    private VideoSurface? _surface;
    private int _presentPending;

    // Frames the player itself has delivered since the page opened: what the no-picture timer waits for.
    private int _playerFrames;

    // Whether the no-picture timer is due: started when the media arrives, paused while the front buffer is gone.
    private bool _noPictureWaiting;

    public PlayerView()
    {
        InitializeComponent();
        Picture.Source = _image;
        _settle = new DispatcherTimer(ResizeSettle, DispatcherPriority.Render, OnResizeSettled, Dispatcher) { IsEnabled = false };
        _position = new DispatcherTimer(PositionInterval, DispatcherPriority.Background, (_, _) => _page?.RefreshPosition(), Dispatcher) { IsEnabled = false };
        _noPicture = new DispatcherTimer(NoPictureTimeout, DispatcherPriority.Background, OnNoPicture, Dispatcher) { IsEnabled = false };
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnScrubStarted));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnScrubCompleted));
        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        ((FrameworkElement)Picture.Parent).SizeChanged += (_, _) =>
        {
            _settle.Stop();
            _settle.Start();
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as PlayerPage);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as PlayerPage);
    }

    /// <summary>The surface's texture size now, or null with none: for tests.</summary>
    internal (int Width, int Height)? SurfaceSize => _surface is { Width: > 0 } s ? (s.Width, s.Height) : null;

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
            old.Player.FrameAvailable -= OnPlayerFrame;
            old.PropertyChanged -= OnPagePropertyChanged;
        }

        // The scrub stops while its page is still known, so the page's IsScrubbing is cleared (QA 14.5).
        StopScrub();
        _page = null;
        _settle.Stop();
        _position.Stop();
        _noPicture.Stop();
        _noPictureWaiting = false;
        ReleaseSurface();
        if (page is not null && IsLoaded)
        {
            _page = page;
            Interlocked.Exchange(ref _playerFrames, 0);
            page.Player.FrameAvailable += OnPlayerFrame;
            page.PropertyChanged += OnPagePropertyChanged;
            _position.IsEnabled = page.IsPlaying;
        }
    }

    // Any thread: a frame from the player itself.
    private void OnPlayerFrame()
    {
        Interlocked.Increment(ref _playerFrames);
        OnFrameAvailable();
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
        if (_page is not { IsClosed: false, IsFailed: false, Media: { } media } page || !_image.IsFrontBufferAvailable)
        {
            return;
        }

        Draw(page, () => EnsureSurface(media), Show, ReleaseSurface);
    }

    /// <summary>
    /// One present's work: build or reuse the surface, copy the frame into it, show it.
    /// <list type="bullet">
    /// <item>Any failure building the surface releases it and puts the page in its Failed state, paused,
    /// never a blank picture or a crash. That covers a failing HRESULT however it's mapped:
    /// <c>COMException</c>, or <c>OutOfMemoryException</c> for out of video memory, or <c>ArgumentException</c>
    /// for a size the GPU won't take.</item>
    /// <item>A frame that can't be copied is skipped; the next one comes.</item>
    /// </list>
    /// </summary>
    internal static void Draw(PlayerPage page, Func<VideoSurface?> ensureSurface, Action<VideoSurface> show, Action release)
    {
        VideoSurface? surface;
        try
        {
            surface = ensureSurface();
        }
#pragma warning disable CA1031 // Every GPU failure here is the page's failure (QA 14.2 round 2): narrowing missed OutOfMemoryException and ArgumentException.
        catch (Exception)
#pragma warning restore CA1031
        {
            release();
            page.ShowFailure();
            return;
        }

        if (surface is null)
        {
            return;
        }

        try
        {
            page.Player.CopyFrame(surface);
        }
#pragma warning disable CA1031 // A frame the player can't copy now (closing, its device lost, any mapped HRESULT) is skipped, never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        show(surface);
    }

    private void Show(VideoSurface surface)
    {
        surface.Flush();
        _image.Lock();
        _image.AddDirtyRect(new Int32Rect(0, 0, surface.Width, surface.Height));
        _image.Unlock();
    }

    // The surface at the size the picture wants now; during a resize the old one stays until the size settles.
    // Null when there's no window to make it on yet.
    private VideoSurface? EnsureSurface(PlayerMedia media)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = (FrameworkElement)Picture.Parent;
        var (width, height) = Fit(media.Width, media.Height, area.ActualWidth * dpi.DpiScaleX, area.ActualHeight * dpi.DpiScaleY);
        if (_surface is { Width: > 0 } current && (_settle.IsEnabled || (current.Width == width && current.Height == height)))
        {
            return current;
        }

        if (_surface is null)
        {
            if (PresentationSource.FromVisual(this) is not HwndSource window)
            {
                return null;
            }

            _surface = new VideoSurface(window.Handle);
        }

        // D3DImage keeps its own reference on the old back buffer until it's given the new one.
        _surface.Resize(width, height);
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.BackBuffer, enableSoftwareFallback: true);
        _image.Unlock();
        return _surface;
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

    // No frame from the player since the file opened (a truncated file, say): the page says it can't be played
    // (story 14.6). The view's own presents (a resize, the media arriving) don't count.
    private void OnNoPicture(object? sender, EventArgs e)
    {
        _noPicture.Stop();
        _noPictureWaiting = false;
        if (Volatile.Read(ref _playerFrames) == 0 && _page is { IsClosed: false, IsFailed: false } page)
        {
            page.ShowFailure();
        }
    }

    // The size held: redraw at it (the present remakes the texture).
    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        OnFrameAvailable();
    }

    // Once the page has its media, the first frame (already shown, paused) is drawn for this view.
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPage.Media))
        {
            _noPicture.Interval = NoPictureTimeout;
            _noPictureWaiting = true;
            if (_image.IsFrontBufferAvailable)
            {
                _noPicture.Start();
            }
            OnFrameAvailable();
        }
        else if (e.PropertyName == nameof(PlayerPage.IsPlaying) && _page is { } page)
        {
            _position.IsEnabled = page.IsPlaying;
        }
    }

    // The page's keys, after the focused control's own: Space plays and pauses, Left and Right seek 5 s,
    // Up and Down change the volume 5 %. Esc is the shell's (it closes the drill-in).
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_page is not { IsClosed: false } page || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                page.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                page.SeekBy(-PlayerPage.SeekStep);
                break;
            case Key.Right:
                page.SeekBy(PlayerPage.SeekStep);
                break;
            case Key.Up:
                page.ChangeVolume(PlayerPage.VolumeStep);
                break;
            case Key.Down:
                page.ChangeVolume(-PlayerPage.VolumeStep);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // Dragging the seek bar's thumb: the page keeps only the latest position, applied once a rendered frame.
    private void OnScrubStarted(object sender, DragStartedEventArgs e)
    {
        if (_page is { } page)
        {
            page.IsScrubbing = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnScrubCompleted(object sender, DragCompletedEventArgs e) => StopScrub();

    private void StopScrub()
    {
        CompositionTarget.Rendering -= OnRendering;
        if (_page is { IsScrubbing: true } page)
        {
            page.IsScrubbing = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => _page?.ApplyPendingScrub();

    // A lock screen or display change loses the D3D9 device: remake the surface on new devices when it's
    // back, and redraw the current frame, paused or playing.
    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e) => FrontBufferChanged(e.NewValue is true);

    /// <summary>
    /// The front buffer went (<paramref name="available"/> false: a lock screen, a display change) or came back.
    /// While it's gone no frame can be shown, so the no-picture timer pauses, and it restarts in full when it's
    /// back (QA 14.6). On its return the surface is remade on new devices and the current frame redrawn.
    /// </summary>
    internal void FrontBufferChanged(bool available)
    {
        if (!available)
        {
            _noPicture.Stop();
            return;
        }

        if (_page is not { IsClosed: false })
        {
            return;
        }

        if (_noPictureWaiting && Volatile.Read(ref _playerFrames) == 0)
        {
            _noPicture.Stop();
            _noPicture.Start();
        }

        ReleaseSurface();
        OnFrameAvailable();
    }
}
