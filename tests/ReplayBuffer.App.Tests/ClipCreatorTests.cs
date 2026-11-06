using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>Fake-service coverage only; written under the builder's test hold, not executed.</summary>
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
                Assert.True(waveform.ActualHeight >= 28 + 10 * 40);
                Assert.Equal(((SolidColorBrush)window.FindResource("AccentFillColorDefaultBrush")).Color, ((SolidColorBrush)waveform.WaveBrush).Color);
                Assert.NotEqual(((SolidColorBrush)waveform.SurfaceBrush).Color, ((SolidColorBrush)waveform.RulerBrush).Color);
                Assert.Contains(Descendants<TextBox>(view), t => t.Text == page.StartText);
                Assert.Equal(10, Descendants<CheckBox>(view).Count());
                Assert.True(((Button)view.FindName("PreviewButton")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((Button)((PlayerView)view.FindName("Video")).FindName("FullScreen")).Visibility);
                var firstStartBox = Descendants<TextBox>(view).First();
                Assert.True(firstStartBox.TransformToAncestor(view).Transform(new Point()).Y >= waveform.TransformToAncestor(view).Transform(new Point()).Y + waveform.ActualHeight);
            }
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
