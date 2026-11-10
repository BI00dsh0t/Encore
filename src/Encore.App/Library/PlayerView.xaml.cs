using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Encore.Platform.Desktop;

namespace Encore.App.Library;

/// <summary>Draws the player page's frames into a shared surface and wires its controls. Details: docs/code-notes.md.</summary>
internal partial class PlayerView
{
    public static readonly DependencyProperty IsClipEditorProperty = DependencyProperty.Register(nameof(IsClipEditor), typeof(bool), typeof(PlayerView), new PropertyMetadata(false));
    public bool IsClipEditor { get => (bool)GetValue(IsClipEditorProperty); set => SetValue(IsClipEditorProperty, value); }
    // How long the picture's size must hold before the texture is remade at it.
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the page waits for its first picture before saying the file can't be played; tests shorten it.</summary>
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
    private ContextMenu? _menu;
    private bool _volumeHover;

    // Frames the player itself has delivered since the page opened: what the no-picture timer waits for.
    private int _playerFrames;

    // Whether the no-picture timer is due: started when the media is there, paused while the front buffer is gone.
    private bool _noPictureWaiting;

    // Whether D3DImage has its front buffer: the one signal the present, the no-picture timer and the handler read.
    private bool _frontBufferAvailable = true;

    public PlayerView()
    {
        InitializeComponent();
        Picture.Source = _image;
        _settle = new DispatcherTimer(ResizeSettle, DispatcherPriority.Render, OnResizeSettled, Dispatcher) { IsEnabled = false };
        _position = new DispatcherTimer(PositionInterval, DispatcherPriority.Background, (_, _) => _page?.RefreshPosition(), Dispatcher) { IsEnabled = false };
        _noPicture = new DispatcherTimer(NoPictureTimeout, DispatcherPriority.Background, OnNoPicture, Dispatcher) { IsEnabled = false };
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnScrubStarted));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnScrubCompleted));
        _frontBufferAvailable = _image.IsFrontBufferAvailable;
        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        ((FrameworkElement)Picture.Parent).SizeChanged += (_, _) =>
        {
            _settle.Stop();
            _settle.Start();
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as PlayerPage);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as PlayerPage);

        // The volume slider slides out while the pointer or keyboard focus is in the speaker's group.
        VolumeGroup.MouseEnter += (_, _) =>
        {
            _volumeHover = true;
            UpdateVolumeShown();
        };
        VolumeGroup.MouseLeave += (_, _) =>
        {
            _volumeHover = false;
            UpdateVolumeShown();
        };
        VolumeGroup.IsKeyboardFocusWithinChanged += (_, _) => UpdateVolumeShown();
    }

    /// <summary>The surface's texture size now, or null with none: for tests.</summary>
    internal (int Width, int Height)? SurfaceSize => _surface is { Width: > 0 } s ? (s.Width, s.Height) : null;

    /// <summary>Focuses the play button, when the page opens.</summary>
    public void FocusFirstControl() => PlayPause.Focus();

    /// <summary>The surface size for a video shown in an area: the video's aspect, never larger than the video, sides even.</summary>
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

        // The scrub stops while its page is still known, so the page's IsScrubbing is cleared.
        StopScrub();
        CloseSettingsMenu();
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

            // A page that opened before this view attached already has its media: wait for its picture from now.
            if (page.Media is not null && !page.IsFailed)
            {
                StartNoPictureWait();
            }
        }
    }

    private void StartNoPictureWait()
    {
        _noPicture.Stop();
        _noPicture.Interval = NoPictureTimeout;
        _noPictureWaiting = true;
        if (_frontBufferAvailable)
        {
            _noPicture.Start();
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
        if (_page is not { IsClosed: false, IsFailed: false, Media: { } media } page || !_frontBufferAvailable)
        {
            return;
        }

        Draw(page, () => EnsureSurface(media), Show, ReleaseSurface);
    }

    /// <summary>One present's work: build or reuse the surface, copy the frame, show it; a surface failure fails the page.</summary>
    internal static void Draw(PlayerPage page, Func<VideoSurface?> ensureSurface, Action<VideoSurface> show, Action release)
    {
        VideoSurface? surface;
        try
        {
            surface = ensureSurface();
        }
#pragma warning disable CA1031 // Every GPU failure here is the page's failure: narrowing missed OutOfMemoryException and ArgumentException.
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

    // The surface at the size the picture wants now; during a resize the old one stays. Null with no window yet.
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

    // No frame since the file opened, such as a cut-off file: the page says it can't be played.
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
            StartNoPictureWait();
            OnFrameAvailable();
        }
        else if (e.PropertyName == nameof(PlayerPage.IsPlaying) && _page is { } page)
        {
            _position.IsEnabled = page.IsPlaying;
        }
    }

    // The page's keys, after the focused control's own: Space, Left and Right, Up and Down. Esc is the shell's.
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
            case Key.F when page.SupportsFullScreen:
                page.FullScreenCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Whether the volume slider shows: for tests.</summary>
    internal bool IsVolumeShown => VolumeBar.Visibility == Visibility.Visible;

    /// <summary>The gear's menu while it's open, else null: for tests.</summary>
    internal ContextMenu? SettingsMenu => _menu is { IsOpen: true } menu ? menu : null;

    /// <summary>Opens the gear's menu: the Tracks in file order with the playing one ticked, then "Open in default player".</summary>
    internal ContextMenu? OpenSettingsMenu()
    {
        if (_page is not { IsClosed: false } page)
        {
            return null;
        }

        CloseSettingsMenu();
        var menu = new ContextMenu { PlacementTarget = Gear, Placement = PlacementMode.Top };
        var itemStyle = (Style)FindResource("EncorePlayerMenuItemStyle");
        menu.Items.Add(new MenuItem
        {
            Header = new TextBlock { Text = "Audio track", Style = (Style)FindResource("EncorePlayerMenuHeadingStyle") },
            Style = itemStyle,
            Focusable = false,
            IsHitTestVisible = false,
        });
        foreach (var track in page.Tracks)
        {
            var item = new MenuItem { Header = track.Name, Style = itemStyle, IsCheckable = true, IsChecked = track.IsPlaying, IsEnabled = track.IsAvailable };
            item.Click += (_, _) => page.SelectedTrack = track;
            menu.Items.Add(item);
        }

        if (page.SupportsExternalActions)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Open in default player", Style = itemStyle, Command = page.OpenInDefaultPlayerCommand });
        }
        menu.Closed += (_, _) => Gear.IsChecked = false;
        _menu = menu;
        menu.IsOpen = true;
        Gear.IsChecked = true;
        return menu;
    }

    // The gear, by click, Enter or Space: opens its menu, or closes it when it's open.
    private void OnGearClick(object sender, RoutedEventArgs e)
    {
        if (_menu is { IsOpen: true })
        {
            CloseSettingsMenu();
            return;
        }

        if (OpenSettingsMenu() is null)
        {
            Gear.IsChecked = false;
        }
    }

    private void CloseSettingsMenu()
    {
        if (_menu is { } menu)
        {
            _menu = null;
            menu.IsOpen = false;
        }

        Gear.IsChecked = false;
    }

    private void UpdateVolumeShown() =>
        VolumeBar.Visibility = _volumeHover || VolumeGroup.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;

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

    // A lock screen or display change loses the D3D9 device: remake the surface and redraw the current frame.
    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e) => FrontBufferChanged(e.NewValue is true);

    /// <summary>The front buffer went or came back: the no-picture timer pauses while it is gone, and the surface is remade.</summary>
    internal void FrontBufferChanged(bool available)
    {
        _frontBufferAvailable = available;
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
