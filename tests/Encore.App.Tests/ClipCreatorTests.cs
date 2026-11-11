using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Encore.App.Library;
using Encore.App.Theme;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

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
    public void TheTimelineMapsTimeToPixelsAndTabStepsThroughItsHandles()
    {
        var scale = new TimelineScale(PlotLeft: 100, ControlWidth: 500, ViewportStart: 10, ViewportEnd: 20);
        Assert.Equal(300, scale.XOf(15));
        Assert.Equal(15, scale.TimeAt(300));
        Assert.Equal(20, scale.TimeAt(900));
        Assert.Equal(0.025, scale.SecondsPerPixel);

        Assert.Equal(TimelineHandle.In, WaveformView.NextHandle(TimelineHandle.Playhead, backwards: false));
        Assert.Equal(TimelineHandle.Out, WaveformView.NextHandle(TimelineHandle.In, backwards: false));
        Assert.Null(WaveformView.NextHandle(TimelineHandle.Out, backwards: false));
        Assert.Equal(TimelineHandle.Playhead, WaveformView.NextHandle(TimelineHandle.In, backwards: true));
    }

    [Fact]
    public async Task MovingSelectionPreservesDurationAndCachesWhileClampingToTheSource()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(45); await viewModel.EnsureThumbnailsAsync();
        viewModel.SetRange(40.125, 45.875); await viewModel.RefreshAsync();
        viewModel.Seek(47);
        var viewport = (viewModel.ViewportStart, viewModel.ViewportEnd);
        var peaks = viewModel.Waveforms; var thumbnails = viewModel.Thumbnails;
        var requests = _media.Requests.Count;
        viewModel.BeginRangeDrag();
        for (var i = 0; i < 100; i++) { viewModel.MoveSelection(40.125, 45.875, i / 100d); }
        viewModel.MoveSelection(40.125, 45.875, 1.2346);
        Assert.Equal(41.360, viewModel.Start, 9); Assert.Equal(47.110, viewModel.End, 9);
        viewModel.MoveSelection(40.125, 45.875, 1000);
        Assert.Equal((84.25, 90d), (viewModel.Start, viewModel.End));
        viewModel.MoveSelection(40.125, 45.875, -1000);
        Assert.Equal((0d, 5.75), (viewModel.Start, viewModel.End));
        viewModel.MoveSelection(40.125, 45.875, 0.3335);
        Assert.Equal(40.459, viewModel.Start, 9); Assert.Equal(46.209, viewModel.End, 9);
        Assert.Equal(5.75, viewModel.End - viewModel.Start, 9);
        Assert.Equal(47, viewModel.Playhead);
        Assert.Equal(viewport, (viewModel.ViewportStart, viewModel.ViewportEnd));
        Assert.Equal(requests, _media.Requests.Count);
        Assert.Same(peaks, viewModel.Waveforms); Assert.Same(thumbnails, viewModel.Thumbnails);
        Assert.False(viewModel.CanExport);
        viewModel.EndRangeDrag(); await viewModel.RefreshAsync();
        Assert.Equal(requests + 1, _media.Requests.Count);
        Assert.Equal(viewModel.StartText, ClipCreatorViewModel.FormatTime(viewModel.Start));
        viewModel.PauseAndCancel(); viewModel.MoveSelection(40.125, 45.875, 1);
        Assert.Equal(40.459, viewModel.Start, 9);
    }

    [Fact]
    public void SelectionBodyDragKeepsClickSeekGripRulerAndDoubleClickRolesDistinct() => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        void Finish(Task task) { Sta.PumpUntil(() => task.IsCompleted, "selection move setup"); task.GetAwaiter().GetResult(); }
        var players = new List<FakePlayer>();
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () =>
        {
            var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(90), ["Game"], true) };
            players.Add(player); return player;
        });
        Finish(viewModel.OpenAsync(0)); Finish(viewModel.EnsureThumbnailsAsync());
        viewModel.Zoom(3); viewModel.SetRange(10, 20); Finish(viewModel.RefreshAsync());
        var view = new ClipCreatorView { DataContext = viewModel };
        ((PlayerView)view.FindName("Video")).FrontBufferChanged(false);
        var window = new Window { Content = view, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var theme = ThemeService.ForWindow(window);
        try
        {
            foreach (var look in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(look); window.Show(); window.UpdateLayout();
                if (look == AppTheme.GamingDark) { viewModel.Zoom(0.5); viewModel.Pan(0.1); Finish(viewModel.RefreshAsync()); }
                var timeline = (WaveformView)view.FindName("Timeline");
                Point At(double time, double y = 48) => new(100 + (time - viewModel.ViewportStart) / (viewModel.ViewportEnd - viewModel.ViewportStart) * (timeline.ActualWidth - 100), y);
                viewModel.SetRange(10, 20); Finish(viewModel.RefreshAsync()); viewModel.Seek(2);
                var peaks = viewModel.Waveforms; var thumbnails = viewModel.Thumbnails;
                Assert.True(timeline.BeginPointer(At(15), 1));
                timeline.MovePointer(new Point(At(15).X + SystemParameters.MinimumHorizontalDragDistance / 2, 48));
                Assert.Equal(2, viewModel.Playhead); Assert.True(viewModel.CanExport);
                timeline.EndPointer(commitClick: true);
                Assert.Equal(15, viewModel.Playhead, 6); Assert.Equal((10d, 20d), (viewModel.Start, viewModel.End));
                Assert.True(timeline.BeginPointer(At(15), 1)); timeline.MovePointer(At(18));
                Assert.Equal((13d, 23d), (viewModel.Start, viewModel.End)); Assert.Equal(15, viewModel.Playhead, 6);
                Assert.False(viewModel.CanExport); Assert.False(viewModel.IsScrubbing);
                timeline.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Sta.PumpUntil(() => viewModel.CanExport, "moved range to resolve after lost capture");
                Assert.Same(peaks, viewModel.Waveforms); Assert.Same(thumbnails, viewModel.Thumbnails);
                // A fast click after a drag must not be mistaken for a waveform double-click.
                Assert.True(timeline.BeginPointer(At(18), 2)); timeline.EndPointer(commitClick: true);
                Assert.Equal((13d, 23d), (viewModel.Start, viewModel.End));
                Assert.True(timeline.BeginPointer(At(18), 1)); timeline.EndPointer(commitClick: true);
                Assert.True(timeline.BeginPointer(At(18), 2));
                Assert.Equal((viewModel.ViewportStart, viewModel.ViewportEnd), (viewModel.Start, viewModel.End));
                viewModel.SetRange(10, 20); Finish(viewModel.RefreshAsync());
                Assert.True(timeline.BeginPointer(At(10), 1)); timeline.MovePointer(At(11)); timeline.EndPointer(commitClick: true);
                Assert.Equal((11d, 20d), (viewModel.Start, viewModel.End)); Finish(viewModel.RefreshAsync());
                Assert.True(timeline.BeginPointer(At(15, 10), 1)); timeline.MovePointer(At(18, 10)); timeline.EndPointer(commitClick: true);
                Assert.Equal(18, viewModel.Playhead, 6); Assert.Equal((11d, 20d), (viewModel.Start, viewModel.End));
                Assert.True(timeline.BeginPointer(At(30), 1)); timeline.MovePointer(At(29)); timeline.EndPointer(commitClick: true);
                Assert.Equal(29, viewModel.Playhead, 6); Assert.Equal((11d, 20d), (viewModel.Start, viewModel.End));
                // The video row moves the same interval as the waveform rows.
                Assert.True(timeline.BeginPointer(At(15.5, timeline.ActualHeight - 20), 1));
                timeline.MovePointer(At(17.5, timeline.ActualHeight - 20)); timeline.EndPointer(commitClick: false);
                Assert.Equal((13d, 22d), (viewModel.Start, viewModel.End)); Finish(viewModel.RefreshAsync());
                Finish(viewModel.PreviewAsync()); viewModel.Preview!.PlayPauseCommand.Execute(null); viewModel.Seek(15); viewModel.Tick();
                Assert.True(timeline.BeginPointer(At(17.5), 1)); timeline.MovePointer(At(18.5));
                Assert.True(players[^1].Disposed); Assert.False(viewModel.Preview!.IsPlaying);
                viewModel.Tick(); Assert.Equal(15, viewModel.Playhead, 6);
                Assert.Equal((14d, 23d), (viewModel.Start, viewModel.End));
                timeline.EndPointer(commitClick: true); Finish(viewModel.RefreshAsync());
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task CalculatedFilmstripSurvivesReopeningAndChangedSourcesMissTheCache()
    {
        var cacheFolder = Path.Combine(_folder, "filmstrip-cache");
        using (var first = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder))
        {
            await first.OpenAsync(0); await first.EnsureThumbnailsAsync();
            Assert.Equal(2, first.Thumbnails.Length);
        }
        Assert.Single(Directory.GetFiles(cacheFolder, "manifest.json", SearchOption.AllDirectories));
        _media.Requests.Clear();
        using (var reopened = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder))
        {
            await reopened.OpenAsync(0); await reopened.EnsureThumbnailsAsync();
            Assert.Equal(2, reopened.Thumbnails.Length);
            Assert.All(reopened.Thumbnails, frame => Assert.True(frame.Image.IsFrozen));
            Assert.DoesNotContain(_media.Requests, request => request.Operation == "prepareThumbnails");
            reopened.Zoom(0.5); reopened.Pan(0.5); reopened.Seek(2);
            await reopened.EnsureThumbnailsAsync();
            Assert.DoesNotContain(_media.Requests, request => request.Operation == "prepareThumbnails");
        }
        File.AppendAllText(Source.Path, "changed");
        _media.Requests.Clear();
        using var changed = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder);
        await changed.OpenAsync(0); await changed.EnsureThumbnailsAsync();
        Assert.Equal(2, changed.Thumbnails.Length);
        Assert.Single(_media.Requests, request => request.Operation == "prepareThumbnails");
    }

    [Fact]
    public async Task CancelledPreparationPublishesNothingAndCorruptCachedFramesAreRebuilt()
    {
        var cacheFolder = Path.Combine(_folder, "filmstrip-cache");
        _media.ThumbnailRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder);
        await cancelled.OpenAsync(0);
        var pending = cancelled.EnsureThumbnailsAsync();
        cancelled.Dispose(); await pending;
        Assert.False(Directory.Exists(cacheFolder));
        _media.ThumbnailRelease.SetResult();
        using (var first = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder))
        { await first.OpenAsync(0); await first.EnsureThumbnailsAsync(); Assert.Equal(2, first.Thumbnails.Length); }
        var jpeg = Directory.GetFiles(cacheFolder, "*.jpg", SearchOption.AllDirectories).First();
        File.WriteAllBytes(jpeg, [0]);
        _media.Requests.Clear();
        using (var rebuilt = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder))
        {
            await rebuilt.OpenAsync(0); await rebuilt.EnsureThumbnailsAsync();
            Assert.Equal(2, rebuilt.Thumbnails.Length);
            Assert.Single(_media.Requests, request => request.Operation == "prepareThumbnails");
        }
        _media.Requests.Clear();
        using var reused = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder);
        await reused.OpenAsync(0); await reused.EnsureThumbnailsAsync();
        Assert.Equal(2, reused.Thumbnails.Length);
        Assert.DoesNotContain(_media.Requests, request => request.Operation == "prepareThumbnails");
    }

    [Fact]
    public async Task ThumbnailEvictionKeepsTheNewEntryAndLeavesUnrelatedFoldersAlone()
    {
        var cacheFolder = Path.Combine(_folder, "filmstrip-cache");
        for (var i = 0; i < 129; i++)
        {
            var old = Directory.CreateDirectory(Path.Combine(cacheFolder, i.ToString("X64")));
            old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        }
        var unrelated = Directory.CreateDirectory(Path.Combine(cacheFolder, "keep-me"));
        File.WriteAllText(Path.Combine(unrelated.FullName, "note.txt"), "keep");
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(), thumbnailCacheFolder: cacheFolder);
        await viewModel.OpenAsync(0); await viewModel.EnsureThumbnailsAsync();
        Assert.Equal(2, viewModel.Thumbnails.Length);
        Assert.Equal(128, Directory.GetDirectories(cacheFolder).Count(path => Path.GetFileName(path).Length == 64));
        Assert.Single(Directory.GetFiles(cacheFolder, "manifest.json", SearchOption.AllDirectories));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated.FullName, "note.txt")));
        Assert.True(File.Exists(Source.Path));
    }

    [Fact]
    public async Task PlaybackLoopsTheLiveTrimRangeAndKeepsPausedSeekingIndependent()
    {
        var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(90), ["Game"], true) };
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => player);
        await viewModel.OpenAsync(0);
        viewModel.SetRange(2, 4);
        viewModel.Seek(8);
        viewModel.Preview!.PlayPauseCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position);
        player.Position = TimeSpan.FromSeconds(3.999); viewModel.Tick();
        Assert.Equal(3.999, viewModel.Playhead, 6);
        player.Position = TimeSpan.FromSeconds(4); viewModel.Tick();
        Assert.Equal(2, viewModel.Playhead);
        Assert.True(viewModel.Preview.IsPlaying);
        viewModel.BeginRangeDrag(); viewModel.SetRange(5, 6);
        player.Position = TimeSpan.FromSeconds(4); viewModel.Tick();
        Assert.Equal(5, viewModel.Playhead);
        player.Position = TimeSpan.FromSeconds(6); viewModel.Tick();
        Assert.Equal(5, viewModel.Playhead);
        Assert.Equal((5d, 6d), (viewModel.Start, viewModel.End));
        viewModel.EndRangeDrag();
        viewModel.Preview.PlayPauseCommand.Execute(null);
        viewModel.Seek(9); viewModel.Tick();
        Assert.Equal(9, viewModel.Playhead);
        Assert.False(viewModel.Preview.IsPlaying);
        await viewModel.RefreshAsync();
    }

    [Fact]
    public void SourceAndRenderedPreviewLoopAtEndWithoutRestartingAfterHide() => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        void Finish(Task task) { Sta.PumpUntil(() => task.IsCompleted, "clip playback setup"); task.GetAwaiter().GetResult(); }
        var players = new List<FakePlayer>();
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () =>
        {
            var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(players.Count == 0 ? 90 : 10), ["Game"], true) };
            players.Add(player); return player;
        });
        Finish(viewModel.OpenAsync(0));
        viewModel.SetRange(80, 90); Finish(viewModel.RefreshAsync());
        viewModel.Preview!.PlayPauseCommand.Execute(null);
        players[0].Seeks.Clear(); players[0].Position = TimeSpan.FromSeconds(90); players[0].RaiseEnded();
        Sta.PumpUntil(() => players[0].Seeks.Count != 0, "source end to loop");
        Assert.Equal(TimeSpan.FromSeconds(80), players[0].Seeks.Last());
        Assert.True(viewModel.Preview.IsPlaying);
        Finish(viewModel.PreviewAsync());
        players[1].Seeks.Clear(); players[1].Position = TimeSpan.FromSeconds(10); players[1].RaiseEnded();
        Sta.PumpUntil(() => players[1].Seeks.Count != 0, "rendered selection end to loop");
        Assert.Equal(TimeSpan.Zero, players[1].Seeks.Last());
        viewModel.Tick();
        Assert.Equal(80, viewModel.Playhead);
        Assert.Contains("Rendered selection", viewModel.PreviewLabel);
        viewModel.BeginRangeDrag(); viewModel.SetRange(82, 86);
        Assert.True(players[1].Disposed);
        Assert.True(viewModel.Preview!.IsPlaying);
        players[0].Position = TimeSpan.FromSeconds(86); viewModel.Tick();
        Assert.Equal(82, viewModel.Playhead);
        viewModel.EndRangeDrag();
        viewModel.PauseAndCancel();
        players[0].Seeks.Clear(); players[0].RaiseEnded(); Sta.Pump(TimeSpan.FromMilliseconds(20));
        Assert.False(viewModel.Preview.IsPlaying);
        Assert.Empty(players[0].Seeks);
        // The shared player stays opt-in: ordinary Library playback still stops at EOF.
        var ordinary = new FakePlayer();
        using var ordinaryViewModel = new PlayerViewModel(Source.Path, "ordinary", ordinary);
        Finish(ordinaryViewModel.OpenAsync()); ordinaryViewModel.PlayPauseCommand.Execute(null); ordinary.RaiseEnded();
        Sta.PumpUntil(() => !ordinaryViewModel.IsPlaying, "ordinary player to stop at end");
        Assert.Empty(ordinary.Seeks);
    });

    [Fact]
    public async Task FilmstripIsPreparedOnceAndSeekingNeverChangesTrim()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(15);
        await viewModel.EnsureThumbnailsAsync();
        Assert.Equal(2, viewModel.Thumbnails.Length);
        Assert.All(viewModel.Thumbnails, frame => Assert.True(frame.Image.IsFrozen));
        var prepared = _media.Requests.Single(r => r.Operation == "prepareThumbnails");
        Assert.False(Directory.Exists(prepared.Destination));
        var images = viewModel.Thumbnails;
        var range = (viewModel.Start, viewModel.End);
        viewModel.SkipForwardCommand.Execute(null);
        Assert.Equal(25, viewModel.Playhead);
        Assert.Equal(range, (viewModel.Start, viewModel.End));
        viewModel.SkipBackCommand.Execute(null);
        Assert.Equal(15, viewModel.Playhead);
        viewModel.Seek(0); viewModel.SkipBackCommand.Execute(null); Assert.Equal(0, viewModel.Playhead);
        viewModel.Seek(viewModel.Duration); viewModel.SkipForwardCommand.Execute(null); Assert.Equal(viewModel.Duration, viewModel.Playhead);
        viewModel.Zoom(0.5); viewModel.Pan(-0.5); viewModel.Nudge(false, 1); viewModel.Format = "mp3";
        await viewModel.RefreshAsync();
        await viewModel.EnsureThumbnailsAsync();
        Assert.Same(images, viewModel.Thumbnails);
        Assert.Single(_media.Requests, r => r.Operation == "prepareThumbnails");
        await Assert.ThrowsAsync<IOException>(() => ClipThumbnailCache.LoadAsync(_folder,
            new(1, Source, viewModel.Duration, [new(0, "../outside.jpg")]), Source, viewModel.Duration, CancellationToken.None));
    }

    [Fact]
    public async Task OptionalThumbnailWorkCancelsBeforeExportAndRetriesAfterResume()
    {
        _media.ThumbnailRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(0);
        var preparation = viewModel.EnsureThumbnailsAsync();
        var scratch = _media.Requests.Single(r => r.Operation == "prepareThumbnails").Destination!;
        viewModel.SetRange(1, 2);
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanExport); // Range queries must remain usable during thumbnail preparation.
        await viewModel.ExportAsync(Path.Combine(_folder, "output.mp4"));
        Assert.Single(_media.Requests, r => r.Operation == "export");
        await preparation;
        Assert.False(Directory.Exists(scratch));
        var retry = viewModel.EnsureThumbnailsAsync();
        viewModel.PauseAndCancel();
        await retry;
        Assert.Empty(viewModel.Thumbnails);
        _media.ThumbnailRelease.SetResult();
        viewModel.Resume();
        await viewModel.EnsureThumbnailsAsync();
        Assert.Equal(2, viewModel.Thumbnails.Length);
    }

    [Fact]
    public async Task CancellingWhileThumbnailWorkerStopsCannotStartAnExport()
    {
        _media.ThumbnailRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _media.ThumbnailStopRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(0);
        var preparation = viewModel.EnsureThumbnailsAsync();
        var export = viewModel.ExportAsync(Path.Combine(_folder, "cancelled.mp4"));
        Assert.True(viewModel.Exporting);
        viewModel.CancelCommand.Execute(null);
        _media.ThumbnailStopRelease.SetResult();
        await export;
        await preparation;
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "export");
        _media.ThumbnailRelease.SetResult();
        await viewModel.EnsureThumbnailsAsync();
    }

    [Fact]
    public async Task MillisecondSelectionSurvivesZoomAndPanAndRequestsFinerSourceBins()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(45);
        Assert.Equal(30, viewModel.Start);
        Assert.Equal(60, viewModel.End);
        viewModel.StartText = "00:00:41.123";
        viewModel.EndText = "00:00:44.987";
        viewModel.Nudge(false, 1);
        viewModel.Zoom(0.5);
        viewModel.Pan(0.2);
        await viewModel.RefreshAsync();
        Assert.Equal(41.124, viewModel.Start, 6);
        Assert.Equal(44.987, viewModel.End, 6);
        Assert.Equal("00:00:41.124", viewModel.StartText);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "waveform");
        var waveform = viewModel.Waveforms[0];
        Assert.Equal(15, waveform.End - waveform.Start, 6);
        Assert.True(waveform.Peaks.Length <= 1600);
        var previous = viewModel.Waveforms;
        viewModel.Seek(41.124);
        viewModel.Zoom(0.0001);
        Assert.Same(previous, viewModel.Waveforms);
        await viewModel.RefreshAsync();
        Assert.InRange(viewModel.Waveforms[0].Peaks.Length, 10, 11);
        Assert.Contains(1f, viewModel.Waveforms[0].Peaks);
        Assert.Contains(0f, viewModel.Waveforms[0].Peaks);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.Equal(viewModel.Start, _media.Requests.Last(r => r.Operation == "range").Start);
        viewModel.SetRange(44, 42);
        Assert.Equal(41.124, viewModel.Start, 6);
        Assert.NotEmpty(viewModel.Status);
        viewModel.SetRange(viewModel.Start, viewModel.End);
        Assert.Empty(viewModel.Status);
    }

    [Fact]
    public async Task WaveformDisplayUsesEachWholeTrackMaximumWithoutChangingCachedAudioPeaks()
    {
        _media.TrackCount = 3;
        static float PeakAt(int track, int index)
        {
            if (track == 3)
            {
                return 0;
            }

            bool isFirstTrack = track == 1;
            if (index == 1000)
            {
                return isFirstTrack ? 0.2f : 0.8f;
            }

            if (index == 45000)
            {
                return isFirstTrack ? 0.1f : 0.4f;
            }

            return 0;
        }

        _media.PeakAt = PeakAt;
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(0);
        viewModel.Zoom(3); await viewModel.RefreshAsync();
        var folder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
        var files = Directory.GetFiles(folder).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(new[] { 1f, 1f, 0f }, viewModel.Waveforms.Select(w => w.Peaks.Max()).ToArray());
        viewModel.Seek(45); viewModel.Zoom(0.001);
        await viewModel.RefreshAsync();
        Assert.Equal(new[] { 0.5f, 0.5f, 0f }, viewModel.Waveforms.Select(w => w.Peaks.Max()).ToArray());
        Assert.All(viewModel.Waveforms.SelectMany(w => w.Peaks), peak => Assert.True(float.IsFinite(peak)));
        viewModel.Pan(0.1); await viewModel.RefreshAsync();
        Assert.Equal(new[] { 0.5f, 0.5f, 0f }, viewModel.Waveforms.Select(w => w.Peaks.Max()).ToArray());
        viewModel.Tracks[0].Included = false; await viewModel.RefreshAsync();
        Assert.Equal(0.5f, viewModel.Waveforms.Single(w => w.StreamIndex == 2).Peaks.Max());
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.DoesNotContain(_media.Requests, r => r.Operation is "waveform" or "preview" or "export");
        foreach (var (path, bytes) in files) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Fact]
    public async Task SelectingTheVisibleRangeKeepsViewportPeaksAndUsesInwardMillisecondBounds()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        viewModel.SelectVisibleRange(); Assert.Equal((0d, 0d), (viewModel.Start, viewModel.End));
        await viewModel.OpenAsync(45);
        var sourcePlayer = viewModel.Preview;
        await viewModel.PreviewAsync();
        Assert.NotSame(sourcePlayer, viewModel.Preview);
        viewModel.Zoom(0.5); viewModel.Pan(0.12345); await viewModel.RefreshAsync();
        var viewport = (viewModel.ViewportStart, viewModel.ViewportEnd);
        var peaks = viewModel.Waveforms;
        var playhead = viewModel.Playhead;
        viewModel.SelectVisibleRange(); await viewModel.RefreshAsync();
        Assert.Equal((39.352, 54.351), (viewModel.Start, viewModel.End));
        Assert.Equal("00:00:14.999", viewModel.SelectedDurationText);
        Assert.Equal(viewport, (viewModel.ViewportStart, viewModel.ViewportEnd));
        Assert.Equal(playhead, viewModel.Playhead);
        Assert.Same(peaks, viewModel.Waveforms);
        Assert.Same(sourcePlayer, viewModel.Preview);
        Assert.Equal((viewModel.Start, viewModel.End), (_media.Requests[^1].Start, _media.Requests[^1].End));
        Assert.Equal([1, 2], _media.Requests[^1].Tracks!);
        viewModel.Pan(-100); viewModel.SelectVisibleRange(); Assert.Equal((0d, 15d), (viewModel.Start, viewModel.End));
        viewModel.Pan(100); viewModel.SelectVisibleRange(); Assert.Equal((75d, 90d), (viewModel.Start, viewModel.End));
        viewModel.Zoom(10); viewModel.SelectVisibleRange(); Assert.Equal((0d, 90d), (viewModel.Start, viewModel.End));
        viewModel.SetRange(1, 2); viewModel.PauseAndCancel(); viewModel.SelectVisibleRange();
        Assert.Equal((1d, 2d), (viewModel.Start, viewModel.End));
        viewModel.Resume(); await viewModel.RefreshAsync();
        _media.ExportRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var export = viewModel.ExportAsync(Path.Combine(_folder, "clip.mp4"));
        try { viewModel.SelectVisibleRange(); Assert.Equal((1d, 2d), (viewModel.Start, viewModel.End)); }
        finally { _media.ExportRelease.SetResult(); await export; }
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        viewModel.Dispose(); viewModel.SelectVisibleRange(); Assert.Equal((1d, 2d), (viewModel.Start, viewModel.End));
        using var tiny = new ClipCreatorViewModel(Source, "tiny", new FakeMedia { Duration = 0.0004 }, () => new FakePlayer());
        await tiny.OpenAsync(0); tiny.SelectVisibleRange();
        Assert.Equal((0d, 0.0004), (tiny.Start, tiny.End));
    }

    [Fact]
    public async Task TrimDragUpdatesSelectionWithoutStartingWorkUntilRelease()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(0);
        var peaks = viewModel.Waveforms;
        var rangeCount = _media.Requests.Count(r => r.Operation == "range");
        var viewportNotifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(viewModel.ViewportStart) or nameof(viewModel.ViewportEnd)) { viewportNotifications++; }
        };
        viewModel.BeginRangeDrag();
        for (var i = 1; i <= 200; i++)
        {
            viewModel.SetRange(i / 100d, 30 - i / 100d);
            await viewModel.RefreshAsync();
        }
        Assert.Equal((2d, 28d), (viewModel.Start, viewModel.End));
        Assert.Equal("00:00:26.000", viewModel.SelectedDurationText);
        Assert.Equal(rangeCount, _media.Requests.Count(r => r.Operation == "range"));
        Assert.Equal(0, viewportNotifications);
        Assert.Same(peaks, viewModel.Waveforms);
        Assert.False(viewModel.Busy);
        Assert.False(viewModel.CanExport);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.CanExport) && viewModel.CanExport) { released.TrySetResult(); }
        };
        viewModel.EndRangeDrag(); await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(rangeCount + 1, _media.Requests.Count(r => r.Operation == "range"));
        Assert.Equal((2d, 28d), (_media.Requests[^1].Start, _media.Requests[^1].End));
        Assert.Same(peaks, viewModel.Waveforms);
        Assert.True(viewModel.CanExport);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.Empty(viewModel.Status);
        viewModel.BeginRangeDrag(); viewModel.SetRange(3, 27); viewModel.PauseAndCancel();
        await viewModel.RefreshAsync();
        Assert.Equal(rangeCount + 1, _media.Requests.Count(r => r.Operation == "range"));
        viewModel.Resume(); await viewModel.RefreshAsync();
        Assert.True(viewModel.CanExport);
        Assert.Equal((3d, 27d), (_media.Requests[^1].Start, _media.Requests[^1].End));
    }

    [Fact]
    public async Task PreparationSurvivesOrdinaryEditsAndCancelledPreparationCanResume()
    {
        _media.PreparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        var opening = viewModel.OpenAsync(0);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        viewModel.SetRange(1.123, 2.456);
        viewModel.Zoom(0.5); viewModel.Pan(0.2); viewModel.Format = "mp3"; viewModel.Tracks[1].Included = false;
        await viewModel.RefreshAsync();
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.Empty(viewModel.Status);
        viewModel.PauseAndCancel();
        await opening;
        var firstFolder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
        Assert.False(Directory.Exists(firstFolder));
        _media.PreparationRelease.SetResult();
        viewModel.Resume();
        await viewModel.RefreshAsync();
        await viewModel.EnsureWaveformsAsync();
        Assert.Equal(2, _media.Requests.Count(r => r.Operation == "prepareWaveforms"));
        Assert.Equal(2, viewModel.Tracks.Count);
        Assert.Equal((1.123, 2.456), (viewModel.Start, viewModel.End));
        Assert.Single(viewModel.Waveforms);
        Assert.Equal(1, viewModel.Waveforms[0].StreamIndex);
        var peaks = viewModel.Waveforms;
        viewModel.PauseAndCancel(); viewModel.Resume(); await viewModel.RefreshAsync();
        Assert.Equal(2, _media.Requests.Count(r => r.Operation == "prepareWaveforms"));
        Assert.NotEmpty(peaks);
    }

    [Fact]
    public async Task CorruptPreparationIsRejectedAndDisposeWaitsForOwnedWorkerCleanup()
    {
        _media.CorruptPreparation = true;
        using (var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(),
            createPreviewFolder: () => throw new IOException("Newer preview failure.")))
        {
            await viewModel.OpenAsync(0);
            Assert.Empty(viewModel.Waveforms);
            Assert.Contains("Waveform preparation failed", viewModel.Status);
            var preparationError = viewModel.Status;
            viewModel.SetRange(2, 1);
            Assert.Contains("non-empty range", viewModel.Status);
            viewModel.SetRange(viewModel.Start, viewModel.End);
            Assert.Equal(preparationError, viewModel.Status);
            viewModel.StartText = "invalid";
            await viewModel.PreviewAsync();
            Assert.Equal("Newer preview failure.", viewModel.Status);
            viewModel.SetRange(viewModel.Start, viewModel.End);
            Assert.Equal("Newer preview failure.", viewModel.Status);
            var folder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Source.Path));
        }
        _media.Requests.Clear();
        _media.CorruptPreparation = false;
        _media.CorruptSummary = true;
        using (var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer()))
        {
            await viewModel.OpenAsync(0);
            Assert.Empty(viewModel.Waveforms);
            Assert.Contains("Waveform preparation failed", viewModel.Status);
        }
        _media.Requests.Clear();
        _media.CorruptSummary = false;
        using (var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer()))
        {
            await viewModel.OpenAsync(0);
            var previous = viewModel.Waveforms;
            var folder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
            viewModel.PauseAndCancel();
            File.WriteAllBytes(Path.Combine(folder, "track-1-0.rbpk"), [0]);
            viewModel.Zoom(0.0001); await viewModel.RefreshAsync();
            Assert.Same(previous, viewModel.Waveforms);
            Assert.Empty(viewModel.Status);
            viewModel.Resume(); await viewModel.RefreshAsync();
            Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
            Assert.Contains("Cannot read waveform cache", viewModel.Status);
            var readError = viewModel.Status;
            viewModel.EndText = "invalid";
            Assert.Contains("Enter end", viewModel.Status);
            viewModel.SetRange(1, 2);
            Assert.Equal(readError, viewModel.Status);
        }
        _media.Requests.Clear();
        _media.PreparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        var opening = pending.OpenAsync(0);
        var scratch = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
        Assert.True(Directory.Exists(scratch));
        pending.Dispose();
        await opening;
        Assert.False(Directory.Exists(scratch));
        Assert.True(File.Exists(Source.Path));
    }

    [Fact]
    public async Task IndependentScrubbingKeepsTrimAndMapsRenderedPreviewBackToSource()
    {
        var players = new List<FakePlayer>();
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () =>
        {
            var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(90), ["Game", "Mic"], true) };
            players.Add(player); return player;
        });
        await viewModel.OpenAsync(0);
        viewModel.SetRange(1, 4); await viewModel.RefreshAsync(); await viewModel.PreviewAsync();
        viewModel.BeginScrub(2.123);
        Assert.Equal(1.123, players[1].Seeks.Last().TotalSeconds, 6);
        players[1].Position = TimeSpan.Zero;
        viewModel.Tick();
        Assert.Equal(2.123, viewModel.Playhead, 6);
        viewModel.Seek(5.678);
        Assert.Equal("Source video", viewModel.PreviewLabel);
        Assert.True(players[1].Disposed);
        Assert.Equal(5.678, players[0].Seeks.Last().TotalSeconds, 6);
        viewModel.EndScrub();
        Assert.False(viewModel.IsScrubbing);
        Assert.Equal((1d, 4d), (viewModel.Start, viewModel.End));
    }

    [Fact]
    public async Task ExportAndRenderedPreviewUseTheSameExplicitTrackSelectionAndMix()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(0);
        Assert.False(viewModel.Preview!.SupportsFullScreen);
        Assert.False(viewModel.Preview.SupportsExternalActions);
        viewModel.SetRange(1.001, 3.999);
        viewModel.Tracks[1].Included = false;
        viewModel.Mix = true;
        await viewModel.RefreshAsync();
        await viewModel.PreviewAsync();
        Assert.False(viewModel.Preview!.SupportsFullScreen);
        Assert.False(viewModel.Preview.SupportsExternalActions);
        var preview = _media.Requests.Last(r => r.Operation == "preview");
        Assert.Equal([1], preview.Tracks!);
        Assert.True(preview.Mix);
        Assert.Contains("Rendered selection", viewModel.PreviewLabel);
        await viewModel.ExportAsync(Path.Combine(_folder, "clip.mp4"));
        var export = _media.Requests.Last(r => r.Operation == "export");
        Assert.Equal(preview.Start, export.Start);
        Assert.Equal(preview.End, export.End);
        Assert.Equal(preview.Tracks, export.Tracks);
        Assert.Equal(preview.Mix, export.Mix);
        viewModel.IsMp3 = true;
        await viewModel.RefreshAsync();
        await viewModel.PreviewAsync();
        Assert.Equal("mp3", _media.Requests.Last(r => r.Operation == "preview").Format);
        Assert.Contains("audio samples", viewModel.EffectiveText);
        viewModel.NoTracksCommand.Execute(null);
        viewModel.IsMp3 = true;
        await viewModel.RefreshAsync();
        Assert.False(viewModel.CanExport);
        viewModel.IsMp4 = true;
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public async Task PreviewFolderFailureIsShownAndLeavesTheEditorUsable()
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer(),
            createPreviewFolder: () => throw new IOException("Temporary storage is unavailable."));
        await viewModel.OpenAsync(0);
        await viewModel.PreviewAsync();
        Assert.Equal("Temporary storage is unavailable.", viewModel.Status);
        Assert.False(viewModel.Busy);
        Assert.False(viewModel.Exporting);
        Assert.True(viewModel.CanExport);
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "preview");
    }

    [Fact]
    public void EmbeddedPlayerDisablesUnsupportedActionsWhileTheOrdinaryPlayerKeepsThem()
    {
        using var embedded = new PlayerViewModel("source.mp4", "source", new FakePlayer(), embedded: true);
        Assert.False(embedded.FullScreenCommand.CanExecute(null));
        Assert.False(embedded.OpenInDefaultPlayerCommand.CanExecute(null));
        Assert.False(embedded.OpenStoreCommand.CanExecute(null));
        embedded.IsFullScreen = true;
        Assert.False(embedded.IsFullScreen);
        using var ordinary = new PlayerViewModel("source.mp4", "source", new FakePlayer());
        Assert.True(ordinary.FullScreenCommand.CanExecute(null));
        ordinary.FullScreenCommand.Execute(null);
        Assert.True(ordinary.IsFullScreen);
    }

    [Fact]
    public void EntryFocusUsesTheEnabledWaveformWhileInitializationIsPending() => Sta.Run(() =>
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        var view = new ClipCreatorView { DataContext = viewModel };
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
        var library = new LibraryViewModel(engine, player: () => new FakePlayer(), clipMedia: _media);
        var item = new LibraryItem("source.mp4", null, source.Length, DateTimeOffset.UtcNow, false);
        var state = engine.State! with { LibraryFolder = _folder, Library = [item] };
        library.Render(state);
        library.Selection.Click(library.List.Rows[0], false, false);
        library.CreateClipCommand.Execute(null);
        Assert.NotNull(library.Playback.Creator);
        var editing = library.Playback.Creator;
        library.Playback.Pause();
        Assert.Same(editing, library.Playback.Creator);
        Assert.True(library.Selection.HasSelection);
        library.DrillIn.Close();
        Assert.Null(library.Playback.Creator);
        Assert.True(library.Selection.HasSelection);
        library.PlayCommand.Execute(null);
        library.Playback.Player!.SeekTo(TimeSpan.FromSeconds(7));
        library.Playback.Player.CreateClipCommand.Execute(null);
        Assert.Null(library.Playback.Player);
        Assert.Equal(source.Path, library.Playback.Creator!.Source.Path);
        library.Render(state with { Library = [] });
        Assert.Null(library.Playback.Creator);
        Assert.False(library.DrillIn.IsOpen);
    }

    [Fact]
    public void CreatorLayoutAndWaveformTokensResolveInBothLooksWithoutBindingErrors() => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        _media.TrackCount = 10;
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        var opening = viewModel.OpenAsync(0);
        Sta.PumpUntil(() => opening.IsCompleted, "fake creator source to open");
        opening.GetAwaiter().GetResult();
        var view = new ClipCreatorView { DataContext = viewModel };
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
                Assert.Equal(28 + 5 * 40 + 48, waveform.ActualHeight);
                Assert.Equal(5, waveform.ScrollMaximum);
                waveform.LaneOffset = 5;
                Assert.Equal(5, waveform.LaneOffset);
                Assert.Equal(((SolidColorBrush)window.FindResource("AccentFillColorDefaultBrush")).Color, ((SolidColorBrush)waveform.WaveBrush).Color);
                Assert.NotEqual(((SolidColorBrush)waveform.SurfaceBrush).Color, ((SolidColorBrush)waveform.RulerBrush).Color);
                Assert.Contains(Descendants<TextBox>(view), t => t.Text == viewModel.StartText);
                Assert.Empty(Descendants<CheckBox>(view));
                Assert.True(((PlayerView)view.FindName("Video")).ActualHeight > 320);
                CommandManager.InvalidateRequerySuggested(); Sta.Pump(TimeSpan.FromMilliseconds(50));
                Assert.True(((Button)view.FindName("PreviewButton")).IsEnabled, $"Preview command {viewModel.CanExport}, busy {viewModel.Busy}, status {viewModel.Status}");
                Assert.Equal(Visibility.Collapsed, ((Button)((PlayerView)view.FindName("Video")).FindName("FullScreen")).Visibility);
                var playerView = (PlayerView)view.FindName("Video");
                Assert.Equal(Visibility.Collapsed, ((Button)playerView.FindName("PlayPause")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Slider)playerView.FindName("SeekBar")).Visibility);
                var play = (Button)view.FindName("TransportPlay");
                var mute = (Button)playerView.FindName("Mute");
                var gear = (System.Windows.Controls.Primitives.ToggleButton)playerView.FindName("Gear");
                Assert.Equal(36, play.ActualWidth); Assert.Equal(36, play.ActualHeight);
                var playIcon = (System.Windows.Shapes.Path)play.Template.FindName("Icon", play);
                Assert.Equal(16, playIcon.Width);
                var playShape = playIcon.Data.ToString();
                viewModel.Preview!.PlayPauseCommand.Execute(null); window.UpdateLayout();
                Assert.NotEqual(playShape, playIcon.Data.ToString());
                viewModel.Preview.PlayPauseCommand.Execute(null); window.UpdateLayout();
                Assert.Equal(playShape, playIcon.Data.ToString());
                var muteIcon = (System.Windows.Shapes.Path)mute.Template.FindName("Icon", mute);
                var volumeShape = muteIcon.Data.ToString();
                viewModel.Preview.IsMuted = true; window.UpdateLayout();
                Assert.NotEqual(volumeShape, muteIcon.Data.ToString());
                viewModel.Preview.IsMuted = false; window.UpdateLayout();
                Assert.Equal(volumeShape, muteIcon.Data.ToString());
                Assert.NotEqual(playShape, ((System.Windows.Shapes.Path)gear.Template.FindName("Icon", gear)).Data.ToString());
                var originalRange = (viewModel.Start, viewModel.End);
                var originalPlayhead = viewModel.Playhead;
                waveform.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.P) { RoutedEvent = Keyboard.KeyDownEvent });
                waveform.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.Equal(originalPlayhead + 0.001, viewModel.Playhead, 6);
                Assert.Equal(originalRange, (viewModel.Start, viewModel.End));
                viewModel.BeginScrub(3);
                waveform.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Assert.False(viewModel.IsScrubbing);
                Assert.Equal(originalRange, (viewModel.Start, viewModel.End));
                viewModel.BeginRangeDrag(); waveform.MoveBoundary(false, 0.5);
                Assert.False(viewModel.CanExport);
                waveform.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Sta.PumpUntil(() => viewModel.CanExport, "trim release to resolve its final range");
                Assert.True(viewModel.CanExport);
                viewModel.Zoom(0.5);
                viewModel.SetRange(2, 10);
                Assert.False(waveform.TrySelectVisibleRange(new Point(50, 40), 2));
                Assert.False(waveform.TrySelectVisibleRange(new Point(200, 10), 2));
                Assert.False(waveform.TrySelectVisibleRange(new Point(200, 40), 1));
                Assert.False(waveform.TrySelectVisibleRange(new Point(waveform.ActualWidth + 1, 40), 2));
                Assert.Equal((2d, 10d), (viewModel.Start, viewModel.End));
                var handleX = 100 + (viewModel.Start - viewModel.ViewportStart) / (viewModel.ViewportEnd - viewModel.ViewportStart) * (waveform.ActualWidth - 100);
                viewModel.BeginScrub(3); viewModel.BeginRangeDrag();
                Assert.True(waveform.TrySelectVisibleRange(new Point(handleX, 40), 2));
                Assert.False(viewModel.IsScrubbing);
                Assert.False(waveform.IsMouseCaptured);
                Assert.Equal(viewModel.ViewportStart, viewModel.Start, 6);
                Assert.Equal(viewModel.ViewportEnd, viewModel.End, 6);
                waveform.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Sta.PumpUntil(() => viewModel.CanExport, "double-click selection to resolve");
                var firstStartBox = (TextBox)view.FindName("StartBox");
                var oldEnd = viewModel.End;
                firstStartBox.Text = "00:00:01.123";
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                firstStartBox.RaiseEvent(enter);
                Assert.True(enter.Handled);
                Assert.Equal(1.123, viewModel.Start, 6);
                Assert.Equal(oldEnd, viewModel.End);
                var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent };
                firstStartBox.RaiseEvent(right);
                Assert.Equal(1.123, viewModel.Start, 6);
                firstStartBox.Text = "00:00:99.999";
                firstStartBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.Equal(1.123, viewModel.Start, 6);
                Assert.NotEmpty(viewModel.Status);
                waveform.MoveBoundary(false, viewModel.End);
                Assert.Equal(1.123, viewModel.Start, 6);
                Assert.Equal(oldEnd, viewModel.End);
                waveform.MoveBoundary(true, viewModel.Start - 0.001);
                Assert.Equal(oldEnd, viewModel.End);
                waveform.MoveBoundary(false, 1.123);
                viewModel.Nudge(false, 1);
                Assert.Equal("00:00:01.124", viewModel.StartText);
                Assert.Equal(ClipCreatorViewModel.FormatTime(viewModel.End - viewModel.Start), viewModel.SelectedDurationText);
                var refresh = viewModel.RefreshAsync();
                Sta.PumpUntil(() => refresh.IsCompleted, "edited range to refresh");
                Assert.True(firstStartBox.TransformToAncestor(view).Transform(new Point()).Y >= waveform.TransformToAncestor(view).Transform(new Point()).Y + waveform.ActualHeight);
            }
            var range = (viewModel.Start, viewModel.End, viewModel.ViewportStart, viewModel.ViewportEnd);
            window.Width = 480; window.UpdateLayout();
            Assert.Equal(range, (viewModel.Start, viewModel.End, viewModel.ViewportStart, viewModel.ViewportEnd));
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
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        await viewModel.OpenAsync(0);
        viewModel.IsMp3 = true;
        viewModel.ExportName = "my selection";
        viewModel.Tracks[1].Included = false;
        await viewModel.RefreshAsync();
        Assert.True(viewModel.SeparateAudioFiles);
        await viewModel.ChooseAndExportAsync(_ => Task.FromResult<string?>(null));
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "export");
        var destination = new TaskCompletionSource<string?>();
        var choosing = viewModel.ChooseAndExportAsync(_ => destination.Task);
        Assert.False(viewModel.CanSave);
        Assert.False(viewModel.CanOpenOptions);
        await viewModel.ChooseAndExportAsync(_ => throw new InvalidOperationException("Duplicate chooser"));
        destination.SetResult(Path.Combine(_folder, viewModel.ExportName));
        await choosing;
        var export = Assert.Single(_media.Requests, r => r.Operation == "export");
        Assert.Equal("mp3", export.Format);
        Assert.Equal([1], export.Tracks!);
        Assert.False(export.Mix);
        Assert.Equal("my selection", viewModel.ExportName);
        viewModel.ExportName = "../source";
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public void SplitMenuAndOwnedOptionsRetainEditsRestoreFocusAndNeverChooseOnDismissal() => Sta.Run(() =>
    {
        using var viewModel = new ClipCreatorViewModel(Source, "source", _media, () => new FakePlayer());
        var opening = viewModel.OpenAsync(0);
        Sta.PumpUntil(() => opening.IsCompleted, "source to open");
        opening.GetAwaiter().GetResult();
        var view = new ClipCreatorView { DataContext = viewModel, ChooseDestination = _ => throw new InvalidOperationException("Dismissal chose a destination") };
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
                viewModel.Tracks[1].Included = false;
                viewModel.ExportName = "retained";
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
            Assert.Equal("mp3", viewModel.Format);
            var refreshed = viewModel.RefreshAsync();
            Sta.PumpUntil(() => refreshed.IsCompleted, "type configuration to refresh");
            var export = (Button)view.FindName("ExportButton");
            export.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("retained", viewModel.ExportName);
            Assert.False(viewModel.Tracks[1].Included);
            Assert.Same(export, System.Windows.Input.FocusManager.GetFocusedElement(window));
            Assert.DoesNotContain(_media.Requests, r => r.Operation is "export" or "preview");
            var preview = viewModel.RefreshAsync();
            Sta.PumpUntil(() => preview.IsCompleted, "retained options to refresh");
            preview = viewModel.PreviewAsync();
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
        model.Library.Selection.Click(model.Library.List.Rows[0], false, false);
        model.Library.CreateClipCommand.Execute(null);
        Sta.PumpUntil(() => model.Library.Playback.Creator is { Busy: false, Metadata: not null }, "source to open");
        var viewModel = model.Library.Playback.Creator!;
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
                    Assert.True(scroll.ScrollableHeight == 0, $"Scroll {scroll.ScrollableHeight}; viewport {scroll.ViewportHeight}; extent {scroll.ExtentHeight}");
                    var video = (PlayerView)view.FindName("Video");
                    Assert.True(video.ActualHeight >= 350, $"Picture allocation {video.ActualHeight}");
                    Assert.True(((Grid)((Border)video.Content).Child).RowDefinitions[0].ActualHeight >= 300);
                    Assert.True(((Button)view.FindName("PreviewButton")).IsVisible);
                    Assert.True(((Button)view.FindName("ExportButton")).IsVisible);
                    var card = (Border)view.FindName("EditorCard");
                    var play = (Button)view.FindName("TransportPlay");
                    var playBounds = play.TransformToAncestor(card).TransformBounds(new Rect(play.RenderSize));
                    Assert.InRange(Math.Abs(playBounds.Left + playBounds.Width / 2 - card.ActualWidth / 2), 0, 1);
                    Assert.Equal(156, ((WaveformView)view.FindName("Timeline")).ActualHeight);
                    Capture(client, $"clip-controls-{look}-{(expanded ? "expanded" : "rail")}.png");
                    var originalHeight = video.ActualHeight;

                    // Extra height goes to the picture; a window grows only as tall as this screen allows.
                    var grow = Math.Min(300, SystemParameters.MaximumWindowTrackHeight - window.Height);
                    window.Height += grow; window.UpdateLayout();
                    Assert.True(video.ActualHeight >= originalHeight + grow - 1, $"Picture {video.ActualHeight} after growing {grow} from {originalHeight}");
                    Assert.Equal(0, scroll.ScrollableHeight);
                    window.Height -= grow; window.UpdateLayout();
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
        model.Library.Selection.Click(model.Library.List.Rows[0], false, false);
        model.Library.CreateClipCommand.Execute(null);
        Sta.PumpUntil(() => model.Library.Playback.Creator is { Busy: false, Metadata: not null }, "source to open");
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
                Assert.Equal(4, toolbar.Children.OfType<Button>().Count());
                Assert.True(scroll.ScrollableHeight > 0);
                foreach (var button in toolbar.Children.OfType<Button>().Concat(new[] { "TransportPlay", "SkipBack", "SkipForward", "PreviewButton" }.Select(name => (Button)view.FindName(name))))
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
        public double Duration { get; set; } = 90;
        public TaskCompletionSource? PreparationRelease { get; set; }
        public TaskCompletionSource? ThumbnailRelease { get; set; }
        public TaskCompletionSource? ThumbnailStopRelease { get; set; }
        public TaskCompletionSource? ExportRelease { get; set; }
        public bool CorruptPreparation { get; set; }
        public bool CorruptSummary { get; set; }
        public Func<int, int, float>? PeakAt { get; set; }
        public async Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Operation == "export" && ExportRelease is { } export) { await export.Task.WaitAsync(token); }
            if (request.Operation == "prepareWaveforms")
            {
                if (PreparationRelease is { } release) { await release.Task.WaitAsync(token); }
                return Prepare(request);
            }
            if (request.Operation == "prepareThumbnails")
            {
                if (ThumbnailRelease is { } release)
                {
                    try { await release.Task.WaitAsync(token); }
                    catch (OperationCanceledException)
                    {
                        if (ThumbnailStopRelease is { } stopped) { await stopped.Task; }
                        throw;
                    }
                }
                var frames = new List<ClipThumbnail>();
                for (var frame = 0; frame < 2; frame++)
                {
                    var pixels = new byte[160 * 90 * 4];
                    for (var y = 0; y < 90; y++)
                    {
                        for (var x = 0; x < 160; x++)
                        {
                            var offset = (y * 160 + x) * 4;
                            pixels[offset] = (byte)(45 + y); pixels[offset + 1] = (byte)(30 + x / 2); pixels[offset + 2] = (byte)(30 + frame * 30); pixels[offset + 3] = 255;
                        }
                    }
                    var image = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 640);
                    var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    var name = $"frame-{frame}.jpg";
                    using (var file = File.Create(Path.Combine(request.Destination!, name))) { encoder.Save(file); }
                    frames.Add(new(frame * Duration / 2, name));
                }
                return new("complete", Thumbnails: new(1, request.Source, Duration, [.. frames]));
            }
            return request.Operation switch
            {
                "probe" => new ClipMediaResponse("complete", Metadata: new(Duration, 1280, 720, 30,
                    Enumerable.Range(1, TrackCount).Select(i => new ClipAudioTrack(i, TrackName(i), 48000, 2)).ToArray())),
                "range" => new ClipMediaResponse("complete", Range: new(request.Start, request.End, request.Format == "mp3" ? "audioSamples48000" : "videoFrames")),
                "waveform" => new ClipMediaResponse("complete", Waveforms: (request.Tracks ?? []).Select(i => new ClipWaveform(request.Start, request.End, i, new float[request.Bins])).ToArray()),
                _ => Render(request),
            };
        }
        private static string TrackName(int index)
        {
            if (index == 1)
            {
                return "Game";
            }

            return index == 2 ? "Mic" : $"Track {index}";
        }

        private ClipMediaResponse Prepare(ClipMediaRequest request)
        {
            var tracks = new List<ClipPeakTrack>();
            for (var track = 1; track <= TrackCount; track++)
            {
                var levels = new List<ClipPeakLevel>();
                var values = Enumerable.Range(0, (int)Math.Ceiling(Duration * 1000)).Select(i => PeakAt?.Invoke(track, i) ?? (i % 1000 == 124 ? 1f : 0f)).ToArray();
                long stride = 1;
                while (true)
                {
                    var name = $"track-{track}-{levels.Count}.rbpk";
                    using (var writer = new BinaryWriter(File.Create(Path.Combine(request.Destination!, name))))
                    {
                        writer.Write(0x4B504252); writer.Write(1); writer.Write(track); writer.Write(0);
                        writer.Write(stride); writer.Write((long)values.Length);
                        foreach (var peak in values) { writer.Write(peak); }
                    }
                    levels.Add(new(name, stride, values.Length));
                    if (values.Length == 1) { break; }
                    values = values.Chunk(16).Select(chunk => chunk.Max()).ToArray();
                    stride *= 16;
                }
                tracks.Add(new(track, [.. levels]));
            }
            if (CorruptPreparation) { File.WriteAllBytes(Path.Combine(request.Destination!, tracks[0].Levels[0].FileName), [0]); }
            if (CorruptSummary)
            {
                using var writer = new BinaryWriter(File.OpenWrite(Path.Combine(request.Destination!, tracks[0].Levels[^1].FileName)));
                writer.BaseStream.Position = 32; writer.Write(float.NaN);
            }
            return new("complete", Cache: new(1, request.Source, Duration, [.. tracks]));
        }
        private static ClipMediaResponse Render(ClipMediaRequest request)
        {
            if (request.Operation == "preview") { File.WriteAllBytes(request.Destination!, [0]); }
            return new("complete", Range: new(request.Start, request.End, request.Format == "mp3" ? "audioSamples48000" : "videoFrames"), Output: request.Destination);
        }
    }
}
