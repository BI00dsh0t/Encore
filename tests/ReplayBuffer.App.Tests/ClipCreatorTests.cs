using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>Fake-service coverage only; no real media, engine or capture hardware.</summary>
[Collection(nameof(WpfCollection))]
public sealed class ClipCreatorTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-creator-tests-").FullName;
    private readonly FakeMedia _media = new();
    private ClipSource Source
    {
        get
        {
            var path = Path.Combine(_folder, "source.mp4");
            if (!File.Exists(path)) { File.WriteAllBytes(path, [1]); }
            var file = new FileInfo(path);
            return new(path, file.Length, file.LastWriteTimeUtc.Ticks);
        }
    }
    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task MillisecondSelectionSurvivesZoomAndPanAndRequestsFinerSourceBins()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(45);
        Assert.Equal(30, page.Start);
        Assert.Equal(60, page.End);
        page.StartText = "00:00:41.123";
        page.EndText = "00:00:44.987";
        page.Nudge(false, 1);
        page.Zoom(0.5);
        page.Pan(0.2);
        await page.RefreshAsync();
        Assert.Equal(41.124, page.Start, 6);
        Assert.Equal(44.987, page.End, 6);
        Assert.Equal("00:00:41.124", page.StartText);
        var waveform = _media.Requests.Last(r => r.Operation == "waveform");
        Assert.Equal(15, waveform.End - waveform.Start, 6);
        Assert.Equal(1600, waveform.Bins);
        Assert.Equal(page.Start, _media.Requests.Last(r => r.Operation == "range").Start);
        page.SetRange(44, 42);
        Assert.Equal(41.124, page.Start, 6);
    }

    [Fact]
    public async Task ExportAndRenderedPreviewUseTheSameExplicitTrackSelectionAndMix()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(0);
        Assert.False(page.Preview!.SupportsFullScreen);
        Assert.False(page.Preview.SupportsExternalActions);
        page.SetRange(1.001, 3.999);
        page.Tracks[1].Included = false;
        page.Mix = true;
        await page.RefreshAsync();
        await page.PreviewAsync();
        Assert.False(page.Preview!.SupportsFullScreen);
        Assert.False(page.Preview.SupportsExternalActions);
        var preview = _media.Requests.Last(r => r.Operation == "preview");
        Assert.Equal([1], preview.Tracks!);
        Assert.True(preview.Mix);
        Assert.Contains("Rendered selection", page.PreviewLabel);
        await page.ExportAsync(Path.Combine(_folder, "clip.mp4"));
        var export = _media.Requests.Last(r => r.Operation == "export");
        Assert.Equal(preview.Start, export.Start);
        Assert.Equal(preview.End, export.End);
        Assert.Equal(preview.Tracks, export.Tracks);
        Assert.Equal(preview.Mix, export.Mix);
        page.IsMp3 = true;
        await page.RefreshAsync();
        await page.PreviewAsync();
        Assert.Equal("mp3", _media.Requests.Last(r => r.Operation == "preview").Format);
        Assert.Contains("audio samples", page.EffectiveText);
        page.NoTracksCommand.Execute(null);
        page.IsMp3 = true;
        await page.RefreshAsync();
        Assert.False(page.CanExport);
        page.IsMp4 = true;
        await page.RefreshAsync();
        Assert.True(page.CanExport);
    }

    [Fact]
    public async Task PreviewFolderFailureIsShownAndLeavesTheEditorUsable()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer(),
            createPreviewFolder: () => throw new IOException("Temporary storage is unavailable."));
        await page.OpenAsync(0);
        await page.PreviewAsync();
        Assert.Equal("Temporary storage is unavailable.", page.Status);
        Assert.False(page.Busy);
        Assert.False(page.Exporting);
        Assert.True(page.CanExport);
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "preview");
    }

    [Fact]
    public void EmbeddedPlayerDisablesUnsupportedActionsWhileTheOrdinaryPlayerKeepsThem()
    {
        using var embedded = new PlayerPage("source.mp4", "source", new FakePlayer(), embedded: true);
        Assert.False(embedded.FullScreenCommand.CanExecute(null));
        Assert.False(embedded.OpenInDefaultPlayerCommand.CanExecute(null));
        Assert.False(embedded.OpenStoreCommand.CanExecute(null));
        embedded.IsFullScreen = true;
        Assert.False(embedded.IsFullScreen);
        using var ordinary = new PlayerPage("source.mp4", "source", new FakePlayer());
        Assert.True(ordinary.FullScreenCommand.CanExecute(null));
        ordinary.FullScreenCommand.Execute(null);
        Assert.True(ordinary.IsFullScreen);
    }

    [Fact]
    public void EntryFocusUsesTheEnabledWaveformWhileInitializationIsPending() => Sta.Run(() =>
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        var view = new ClipCreatorView { DataContext = page };
        var window = new Window { Content = view, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        ThemeService.ForWindow(window).Apply(AppTheme.Fluent);
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.False(((Button)view.FindName("PreviewButton")).IsEnabled);
            var timeline = (WaveformView)view.FindName("Timeline");
            Assert.True(timeline.IsEnabled && timeline.Focusable);
            view.FocusFirstControl();
            Assert.Same(timeline, System.Windows.Input.FocusManager.GetFocusedElement(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BothEntryPointsKeepLibrarySelectionAndBackOrDisappearanceClosesTheCreator()
    {
        var engine = new FakeEngineSession();
        var source = Source;
        var library = new LibraryPage(engine, player: () => new FakePlayer(), clipMedia: _media);
        var item = new LibraryItem("source.mp4", null, source.Length, DateTimeOffset.UtcNow, false);
        var state = engine.State! with { LibraryFolder = _folder, Library = [item] };
        library.Render(state);
        library.Click(library.Rows[0], false, false);
        library.CreateClipCommand.Execute(null);
        Assert.NotNull(library.Creator);
        var editing = library.Creator;
        library.PausePlayer();
        Assert.Same(editing, library.Creator);
        Assert.True(library.HasSelection);
        library.DrillIn.Close();
        Assert.Null(library.Creator);
        Assert.True(library.HasSelection);
        library.PlayCommand.Execute(null);
        library.Player!.SeekTo(TimeSpan.FromSeconds(7));
        library.Player.CreateClipCommand.Execute(null);
        Assert.Null(library.Player);
        Assert.Equal(source.Path, library.Creator!.Source.Path);
        library.Render(state with { Library = [] });
        Assert.Null(library.Creator);
        Assert.False(library.DrillIn.IsOpen);
    }

    [Fact]
    public void CreatorLayoutAndWaveformTokensResolveInBothLooksWithoutBindingErrors() => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        _media.TrackCount = 10;
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        var opening = page.OpenAsync(0);
        Sta.PumpUntil(() => opening.IsCompleted, "fake creator source to open");
        opening.GetAwaiter().GetResult();
        var view = new ClipCreatorView { DataContext = page };
        ((PlayerView)view.FindName("Video")).FrontBufferChanged(false);
        var window = new Window { Content = view, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var themes = ThemeService.ForWindow(window);
        using var errors = new StringWriter();
        using var listener = new TextWriterTraceListener(errors);
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            foreach (var theme in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                themes.Apply(theme);
                window.Show(); window.UpdateLayout(); Sta.Pump(TimeSpan.FromMilliseconds(50));
                var waveform = Descendants<WaveformView>(view).Single();
                Assert.True(waveform.ActualWidth > 400);
                Assert.Equal(28 + 5 * 24, waveform.ActualHeight);
                Assert.Equal(5, waveform.ScrollMaximum);
                waveform.LaneOffset = 5;
                Assert.Equal(5, waveform.LaneOffset);
                Assert.Equal(((SolidColorBrush)window.FindResource("AccentFillColorDefaultBrush")).Color, ((SolidColorBrush)waveform.WaveBrush).Color);
                Assert.NotEqual(((SolidColorBrush)waveform.SurfaceBrush).Color, ((SolidColorBrush)waveform.RulerBrush).Color);
                Assert.Contains(Descendants<TextBox>(view), t => t.Text == page.StartText);
                Assert.Empty(Descendants<CheckBox>(view));
                Assert.True(((PlayerView)view.FindName("Video")).ActualHeight > 400);
                CommandManager.InvalidateRequerySuggested(); Sta.Pump(TimeSpan.FromMilliseconds(50));
                Assert.True(((Button)view.FindName("PreviewButton")).IsEnabled, $"Preview command {page.CanExport}, busy {page.Busy}, status {page.Status}");
                Assert.Equal(Visibility.Collapsed, ((Button)((PlayerView)view.FindName("Video")).FindName("FullScreen")).Visibility);
                var playerView = (PlayerView)view.FindName("Video");
                var play = (Button)playerView.FindName("PlayPause");
                var mute = (Button)playerView.FindName("Mute");
                var gear = (System.Windows.Controls.Primitives.ToggleButton)playerView.FindName("Gear");
                Assert.Equal(34, play.ActualWidth); Assert.Equal(32, play.ActualHeight);
                var playIcon = (System.Windows.Shapes.Path)play.Template.FindName("Icon", play);
                Assert.Equal(16, playIcon.Width);
                var playShape = playIcon.Data.ToString();
                page.Preview!.PlayPauseCommand.Execute(null); window.UpdateLayout();
                Assert.NotEqual(playShape, playIcon.Data.ToString());
                page.Preview.PlayPauseCommand.Execute(null); window.UpdateLayout();
                Assert.Equal(playShape, playIcon.Data.ToString());
                var muteIcon = (System.Windows.Shapes.Path)mute.Template.FindName("Icon", mute);
                var volumeShape = muteIcon.Data.ToString();
                page.Preview.IsMuted = true; window.UpdateLayout();
                Assert.NotEqual(volumeShape, muteIcon.Data.ToString());
                page.Preview.IsMuted = false; window.UpdateLayout();
                Assert.Equal(volumeShape, muteIcon.Data.ToString());
                Assert.NotEqual(playShape, ((System.Windows.Shapes.Path)gear.Template.FindName("Icon", gear)).Data.ToString());
                var firstStartBox = (TextBox)view.FindName("StartBox");
                var oldEnd = page.End;
                firstStartBox.Text = "00:00:01.123";
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                firstStartBox.RaiseEvent(enter);
                Assert.True(enter.Handled);
                Assert.Equal(1.123, page.Start, 6);
                Assert.Equal(oldEnd, page.End);
                var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent };
                firstStartBox.RaiseEvent(right);
                Assert.Equal(1.123, page.Start, 6);
                firstStartBox.Text = "00:00:99.999";
                firstStartBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.Equal(1.123, page.Start, 6);
                Assert.NotEmpty(page.Status);
                waveform.MoveBoundary(false, page.End);
                Assert.Equal(1.123, page.Start, 6);
                Assert.Equal(oldEnd, page.End);
                waveform.MoveBoundary(true, page.Start - 0.001);
                Assert.Equal(oldEnd, page.End);
                waveform.MoveBoundary(false, 1.123);
                page.Nudge(false, 1);
                Assert.Equal("00:00:01.124", page.StartText);
                Assert.Equal(ClipCreatorPage.FormatTime(page.End - page.Start), page.SelectedDurationText);
                var refresh = page.RefreshAsync();
                Sta.PumpUntil(() => refresh.IsCompleted, "edited range to refresh");
                Assert.True(firstStartBox.TransformToAncestor(view).Transform(new Point()).Y >= waveform.TransformToAncestor(view).Transform(new Point()).Y + waveform.ActualHeight);
            }
            var range = (page.Start, page.End, page.ViewportStart, page.ViewportEnd);
            window.Width = 480; window.UpdateLayout();
            Assert.Equal(range, (page.Start, page.End, page.ViewportStart, page.ViewportEnd));
            window.Width = 100; window.UpdateLayout(); Sta.Pump(TimeSpan.FromMilliseconds(50));
            Assert.True(((WaveformView)view.FindName("Timeline")).ActualWidth < 100);
            listener.Flush();
            Assert.Equal("", errors.ToString());
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
            window.Close();
        }
    });

    [Fact]
    public async Task ChooserCancellationAndDuplicatePressesPreserveConfigurationAndStartOnlyOneExport()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(0);
        page.IsMp3 = true;
        page.ExportName = "my selection";
        page.Tracks[1].Included = false;
        await page.RefreshAsync();
        Assert.True(page.SeparateAudioFiles);
        await page.ChooseAndExportAsync(_ => Task.FromResult<string?>(null));
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "export");
        var destination = new TaskCompletionSource<string?>();
        var choosing = page.ChooseAndExportAsync(_ => destination.Task);
        Assert.False(page.CanSave);
        Assert.False(page.CanOpenOptions);
        await page.ChooseAndExportAsync(_ => throw new InvalidOperationException("Duplicate chooser"));
        destination.SetResult(Path.Combine(_folder, page.ExportName));
        await choosing;
        var export = Assert.Single(_media.Requests, r => r.Operation == "export");
        Assert.Equal("mp3", export.Format);
        Assert.Equal([1], export.Tracks!);
        Assert.False(export.Mix);
        Assert.Equal("my selection", page.ExportName);
        page.ExportName = "../source";
        Assert.False(page.CanSave);
    }

    [Fact]
    public void SplitMenuAndOwnedOptionsRetainEditsRestoreFocusAndNeverChooseOnDismissal() => Sta.Run(() =>
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        var opening = page.OpenAsync(0);
        Sta.PumpUntil(() => opening.IsCompleted, "source to open");
        opening.GetAwaiter().GetResult();
        var view = new ClipCreatorView { DataContext = page, ChooseDestination = _ => throw new InvalidOperationException("Dismissal chose a destination") };
        ((PlayerView)view.FindName("Video")).FrontBufferChanged(false);
        var window = new Window { Content = view, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var theme = ThemeService.ForWindow(window);
        void Options(ClipExportDialog dialog)
        {
            ThemeService.ForWindow(dialog).Apply(AppTheme.GamingDark);
            dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
            {
                dialog.UpdateLayout();
                Capture((FrameworkElement)dialog.Content, "clip-options-GamingDark.png");
                Assert.Same(window, dialog.Owner);
                Assert.Equal(2, Descendants<CheckBox>(dialog).Count());
                page.Tracks[1].Included = false;
                page.ExportName = "retained";
                dialog.Close();
            });
        }
        ClipExportDialog.Showing += Options;
        try
        {
            theme.Apply(AppTheme.Fluent);
            window.Show(); window.UpdateLayout();
            var arrow = (Button)view.FindName("ExportArrow");
            arrow.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var menu = arrow.ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.Equal(2, menu.Items.Count);
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Assert.Equal("mp3", page.Format);
            var refreshed = page.RefreshAsync();
            Sta.PumpUntil(() => refreshed.IsCompleted, "type configuration to refresh");
            var export = (Button)view.FindName("ExportButton");
            export.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("retained", page.ExportName);
            Assert.False(page.Tracks[1].Included);
            Assert.Same(export, System.Windows.Input.FocusManager.GetFocusedElement(window));
            Assert.DoesNotContain(_media.Requests, r => r.Operation is "export" or "preview");
            var preview = page.RefreshAsync();
            Sta.PumpUntil(() => preview.IsCompleted, "retained options to refresh");
            preview = page.PreviewAsync();
            Sta.PumpUntil(() => preview.IsCompleted, "selection preview");
            var request = _media.Requests.Last(r => r.Operation == "preview");
            Assert.Equal("mp3", request.Format);
            Assert.Equal([1], request.Tracks!);
        }
        finally { ClipExportDialog.Showing -= Options; window.Close(); }
    });

    [Fact]
    public void ReferenceClientAreaKeepsTwoTrackCreatorActionsVisibleInsideTheActualShell() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var source = Source;
        engine.State = engine.State! with { LibraryFolder = _folder, Library = [new LibraryItem("source.mp4", null, source.Length, DateTimeOffset.UtcNow, false)] };
        var model = new MainViewModel(engine, action => action(), player: () => new FakePlayer(), clipMedia: _media);
        model.Library.Click(model.Library.Rows[0], false, false);
        model.Library.CreateClipCommand.Execute(null);
        Sta.PumpUntil(() => model.Library.Creator is { Busy: false, Metadata: not null }, "source to open");
        var page = model.Library.Creator!;
        var view = (ClipCreatorView)((LibraryView)model.Library.View).FindName("CreatorContent");
        ((PlayerView)view.FindName("Video")).FrontBufferChanged(false);
        var window = new MainWindow { DataContext = model, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var themes = ThemeService.ForWindow(window);
        try
        {
            foreach (var look in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                themes.Apply(look); window.Show(); window.UpdateLayout();
                var client = (Grid)window.Content;
                window.Width += 960 - client.ActualWidth;
                window.Height += 900 - client.ActualHeight;
                foreach (var expanded in new[] { true, false })
                {
                    model.IsNavigationExpanded = expanded;
                    window.UpdateLayout();
                    var scroll = (ScrollViewer)view.Content;
                    Assert.True(scroll.ScrollableHeight == 0, $"Scroll {scroll.ScrollableHeight}; viewport {scroll.ViewportHeight}; extent {scroll.ExtentHeight}; header {((FrameworkElement)window.FindName("PageHeader")).ActualHeight}; status {((FrameworkElement)window.FindName("StatusHeader")).ActualHeight}; client {client.ActualHeight}; children " + string.Join(", ", ((StackPanel)scroll.Content).Children.OfType<FrameworkElement>().Select(c => c.GetType().Name + "=" + c.ActualHeight)));
                    var video = (PlayerView)view.FindName("Video");
                    Assert.True(video.ActualHeight >= 470);
                    Assert.True(((Grid)((Border)video.Content).Child).RowDefinitions[0].ActualHeight >= 390);
                    Assert.True(((Button)view.FindName("PreviewButton")).IsVisible);
                    Assert.True(((Button)view.FindName("ExportButton")).IsVisible);
                    Assert.Equal(76, ((WaveformView)view.FindName("Timeline")).ActualHeight);
                    Capture(client, $"clip-controls-{look}-{(expanded ? "expanded" : "rail")}.png");
                }
            }
        }
        finally { window.CloseForQuit(); }
    });

    [Fact]
    public void MinimumShellSizeWrapsEveryTimelineActionWithinTheScrollableViewport() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var source = Source;
        engine.State = engine.State! with { LibraryFolder = _folder, Library = [new LibraryItem("source.mp4", null, source.Length, DateTimeOffset.UtcNow, false)] };
        var model = new MainViewModel(engine, action => action(), player: () => new FakePlayer(), clipMedia: _media);
        model.Library.Click(model.Library.Rows[0], false, false);
        model.Library.CreateClipCommand.Execute(null);
        Sta.PumpUntil(() => model.Library.Creator is { Busy: false, Metadata: not null }, "source to open");
        var view = (ClipCreatorView)((LibraryView)model.Library.View).FindName("CreatorContent");
        ((PlayerView)view.FindName("Video")).FrontBufferChanged(false);
        var window = new MainWindow { DataContext = model, Width = 480, Height = 320, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var themes = ThemeService.ForWindow(window);
        try
        {
            foreach (var look in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                themes.Apply(look); window.Show(); window.UpdateLayout();
                var scroll = (ScrollViewer)view.Content;
                var viewport = Descendants<ScrollContentPresenter>(scroll).First();
                var toolbar = (WrapPanel)view.FindName("TimelineToolbar");
                Assert.True(model.IsNavigationExpanded);
                Assert.Equal(5, toolbar.Children.OfType<Button>().Count());
                Assert.True(scroll.ScrollableHeight > 0);
                foreach (var button in toolbar.Children.OfType<Button>())
                {
                    button.BringIntoView(); window.UpdateLayout();
                    var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1, $"Action clipped horizontally: {bounds}, viewport {viewport.RenderSize}");
                    Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1, $"Action cannot scroll into view: {bounds}, viewport {viewport.RenderSize}");
                }
                var rangePanel = Descendants<ClipRangePanel>(view).Single();
                foreach (FrameworkElement group in rangePanel.Children)
                {
                    group.BringIntoView(); window.UpdateLayout();
                    var bounds = group.TransformToAncestor(viewport).TransformBounds(new Rect(group.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1, $"Range group clipped horizontally: {bounds}, viewport {viewport.RenderSize}");
                    Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1, $"Range group cannot scroll into view: {bounds}, viewport {viewport.RenderSize}");
                }
                Capture((FrameworkElement)window.Content, $"clip-controls-{look}-narrow.png");
            }
        }
        finally { window.CloseForQuit(); }
    });

    private static void Capture(FrameworkElement view, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle((Brush)view.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(view.RenderSize));
        }
        bitmap.Render(background);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(output);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) { yield return match; }
            foreach (var nested in Descendants<T>(child)) { yield return nested; }
        }
    }

    private sealed class FakeMedia : IClipMediaService
    {
        public List<ClipMediaRequest> Requests { get; } = [];
        public int TrackCount { get; set; } = 2;
        public Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(request.Operation switch
            {
                "probe" => new ClipMediaResponse("complete", Metadata: new(90, 1280, 720, 30,
                    Enumerable.Range(1, TrackCount).Select(i => new ClipAudioTrack(i, i == 1 ? "Game" : i == 2 ? "Mic" : $"Track {i}", 48000, 2)).ToArray())),
                "range" => new ClipMediaResponse("complete", Range: new(request.Start, request.End, request.Format == "mp3" ? "audioSamples48000" : "videoFrames")),
                "waveform" => new ClipMediaResponse("complete", Waveforms: (request.Tracks ?? []).Select(i => new ClipWaveform(request.Start, request.End, i, new float[request.Bins])).ToArray()),
                _ => Render(request),
            });
        }
        private static ClipMediaResponse Render(ClipMediaRequest request)
        {
            if (request.Operation == "preview") { File.WriteAllBytes(request.Destination!, [0]); }
            return new("complete", Range: new(request.Start, request.End, request.Format == "mp3" ? "audioSamples48000" : "videoFrames"), Output: request.Destination);
        }
    }
}
