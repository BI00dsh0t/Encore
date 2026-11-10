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

/// <summary>The player page's view: it draws the player's frames into a shared surface. Details: docs/code-notes.md.</summary>
internal partial class PlayerView
{
    public static readonly DependencyProperty IsClipEditorProperty = DependencyProperty.Register(
        nameof(IsClipEditor), typeof(bool), typeof(PlayerView), new PropertyMetadata(false));

    // How long the picture's size must hold before the texture is remade at it.
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(150);

    // How often the seek bar and time follow the player while it plays.
    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(100);

    private readonly D3DImage _image = new();
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _position;
    private readonly NoPictureWatch _noPicture;
    private PlayerPage? _page;
    private VideoSurface? _surface;
    private int _presentPending;
    private ContextMenu? _menu;
    private bool _volumeHover;

    // Whether D3DImage has its front buffer; nothing can be presented without it.
    private bool _frontBufferAvailable;

    public PlayerView()
    {
        InitializeComponent();
        Picture.Source = _image;
        _settle = new DispatcherTimer(ResizeSettle, DispatcherPriority.Render, OnResizeSettled, Dispatcher) { IsEnabled = false };
        _position = new DispatcherTimer(PositionInterval, DispatcherPriority.Background, OnPositionTick, Dispatcher) { IsEnabled = false };
        _noPicture = new NoPictureWatch(Dispatcher, OnNoPicture);
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnScrubStarted));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnScrubCompleted));
        _frontBufferAvailable = _image.IsFrontBufferAvailable;
        if (!_frontBufferAvailable)
        {
            _noPicture.Pause();
        }

        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        PictureArea.SizeChanged += OnPictureAreaSizeChanged;
        DataContextChanged += (_, e) => Attach(e.NewValue as PlayerPage);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as PlayerPage);

        // The volume slider slides out while the pointer or keyboard focus is in the speaker's group.
        VolumeGroup.MouseEnter += (_, _) => SetVolumeHover(true);
        VolumeGroup.MouseLeave += (_, _) => SetVolumeHover(false);
        VolumeGroup.IsKeyboardFocusWithinChanged += (_, _) => UpdateVolumeShown();
    }

    /// <summary>Whether this is the clip creator's preview, which hides the seek bar, the play button and the time.</summary>
    public bool IsClipEditor
    {
        get => (bool)GetValue(IsClipEditorProperty);
        set => SetValue(IsClipEditorProperty, value);
    }

    /// <summary>The surface's texture size now, or null with none: for tests.</summary>
    internal (int Width, int Height)? SurfaceSize => _surface is { Width: > 0 } surface ? (surface.Width, surface.Height) : null;

    /// <summary>Whether the volume slider shows: for tests.</summary>
    internal bool IsVolumeShown => VolumeBar.Visibility == Visibility.Visible;

    /// <summary>The gear's menu while it's open, else null: for tests.</summary>
    internal ContextMenu? SettingsMenu => _menu is { IsOpen: true } menu ? menu : null;

    // The space the video is fitted into.
    private FrameworkElement PictureArea => (FrameworkElement)Picture.Parent;

    /// <summary>Focuses the play button, when the page opens.</summary>
    public void FocusFirstControl() => PlayPause.Focus();

    /// <summary>Opens the gear's menu: the Tracks in file order with the playing one ticked, then "Open in default player".</summary>
    internal ContextMenu? OpenSettingsMenu()
    {
        if (_page is not { IsClosed: false } page)
        {
            return null;
        }

        CloseSettingsMenu();
        var menu = BuildSettingsMenu(page);
        menu.Closed += (_, _) => Gear.IsChecked = false;
        _menu = menu;
        menu.IsOpen = true;
        Gear.IsChecked = true;
        return menu;
    }

    /// <summary>The front buffer went or came back: the no-picture wait pauses while it is gone, and the surface is remade.</summary>
    internal void FrontBufferChanged(bool available)
    {
        _frontBufferAvailable = available;
        if (!available)
        {
            _noPicture.Pause();
            return;
        }

        _noPicture.Resume();
        if (_page is { IsClosed: false })
        {
            ReleaseSurface();
            RequestPresent();
        }
    }

    private void Attach(PlayerPage? page)
    {
        if (ReferenceEquals(page, _page))
        {
            return;
        }

        Detach();
        if (page is null || !IsLoaded)
        {
            return;
        }

        _page = page;
        _noPicture.ForgetFrames();
        page.Player.FrameAvailable += OnPlayerFrame;
        page.PropertyChanged += OnPagePropertyChanged;
        _position.IsEnabled = page.IsPlaying;

        // A page that opened before this view attached already has its media: wait for its picture from now.
        if (page.Media is not null && !page.IsFailed)
        {
            _noPicture.Start();
        }
    }

    private void Detach()
    {
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
        _noPicture.Cancel();
        ReleaseSurface();
    }

    // Any thread: a frame from the player itself.
    private void OnPlayerFrame()
    {
        _noPicture.FrameArrived();
        RequestPresent();
    }

    // Any thread: at most one present waits on the UI thread at a time.
    private void RequestPresent()
    {
        if (Interlocked.Exchange(ref _presentPending, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, Present);
        }
    }

    private void Present()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        if (_page is { IsClosed: false, IsFailed: false, Media: { } media } page && _frontBufferAvailable)
        {
            page.DrawFrame(() => EnsureSurface(media), Show, ReleaseSurface);
        }
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
        var area = PictureArea;
        var (width, height) = PictureFit.Size(media.Width, media.Height, area.ActualWidth * dpi.DpiScaleX, area.ActualHeight * dpi.DpiScaleY);
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
    private void OnNoPicture()
    {
        if (_page is { IsClosed: false, IsFailed: false } page)
        {
            page.ShowFailure();
        }
    }

    private void OnPositionTick(object? sender, EventArgs e) => _page?.RefreshPosition();

    // The texture is remade only once the size has held for a moment.
    private void OnPictureAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _settle.Stop();
        _settle.Start();
    }

    // The size held: redraw at it (the present remakes the texture).
    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        RequestPresent();
    }

    // Once the page has its media, the first frame (already shown, paused) is drawn for this view.
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPage.Media))
        {
            _noPicture.Start();
            RequestPresent();
        }
        else if (e.PropertyName == nameof(PlayerPage.IsPlaying) && _page is { } page)
        {
            _position.IsEnabled = page.IsPlaying;
        }
    }

    // The page's keys, after the focused control's own; they take no modifier. Esc is the shell's.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && _page?.HandleKey(e.Key) == true)
        {
            e.Handled = true;
        }
    }

    private ContextMenu BuildSettingsMenu(PlayerPage page)
    {
        var menu = new ContextMenu { PlacementTarget = Gear, Placement = PlacementMode.Top };
        var itemStyle = (Style)FindResource("EncorePlayerMenuItemStyle");
        menu.Items.Add(HeadingItem("Audio track", itemStyle));
        foreach (var track in page.Tracks)
        {
            menu.Items.Add(TrackItem(page, track, itemStyle));
        }

        if (page.SupportsExternalActions)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Open in default player", Style = itemStyle, Command = page.OpenInDefaultPlayerCommand });
        }

        return menu;
    }

    // A caption inside the menu: it can't be chosen or focused.
    private MenuItem HeadingItem(string text, Style itemStyle) => new()
    {
        Header = new TextBlock { Text = text, Style = (Style)FindResource("EncorePlayerMenuHeadingStyle") },
        Style = itemStyle,
        Focusable = false,
        IsHitTestVisible = false,
    };

    private static MenuItem TrackItem(PlayerPage page, PlayerTrack track, Style itemStyle)
    {
        var item = new MenuItem
        {
            Header = track.Name,
            Style = itemStyle,
            IsCheckable = true,
            IsChecked = track.IsPlaying,
            IsEnabled = track.IsAvailable,
        };
        item.Click += (_, _) => page.SelectedTrack = track;
        return item;
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

    private void SetVolumeHover(bool hover)
    {
        _volumeHover = hover;
        UpdateVolumeShown();
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
    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        FrontBufferChanged(e.NewValue is true);
}
