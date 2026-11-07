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
    public async Task PlaybackLoopsTheLiveTrimRangeAndKeepsPausedSeekingIndependent()
    {
        var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(90), ["Game"], true) };
        using var page = new ClipCreatorPage(Source, "source", _media, () => player);
        await page.OpenAsync(0);
        page.SetRange(2, 4);
        page.Seek(8);
        page.Preview!.PlayPauseCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position);
        player.Position = TimeSpan.FromSeconds(3.999); page.Tick();
        Assert.Equal(3.999, page.Playhead, 6);
        player.Position = TimeSpan.FromSeconds(4); page.Tick();
        Assert.Equal(2, page.Playhead);
        Assert.True(page.Preview.IsPlaying);
        page.BeginRangeDrag(); page.SetRange(5, 6);
        player.Position = TimeSpan.FromSeconds(4); page.Tick();
        Assert.Equal(5, page.Playhead);
        player.Position = TimeSpan.FromSeconds(6); page.Tick();
        Assert.Equal(5, page.Playhead);
        Assert.Equal((5d, 6d), (page.Start, page.End));
        page.EndRangeDrag();
        page.Preview.PlayPauseCommand.Execute(null);
        page.Seek(9); page.Tick();
        Assert.Equal(9, page.Playhead);
        Assert.False(page.Preview.IsPlaying);
        await page.RefreshAsync();
    }

    [Fact]
    public void SourceAndRenderedPreviewLoopAtEndWithoutRestartingAfterHide() => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        void Finish(Task task) { Sta.PumpUntil(() => task.IsCompleted, "clip playback setup"); task.GetAwaiter().GetResult(); }
        var players = new List<FakePlayer>();
        using var page = new ClipCreatorPage(Source, "source", _media, () =>
        {
            var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(players.Count == 0 ? 90 : 10), ["Game"], true) };
            players.Add(player); return player;
        });
        Finish(page.OpenAsync(0));
        page.SetRange(80, 90); Finish(page.RefreshAsync());
        page.Preview!.PlayPauseCommand.Execute(null);
        players[0].Seeks.Clear(); players[0].Position = TimeSpan.FromSeconds(90); players[0].RaiseEnded();
        Sta.PumpUntil(() => players[0].Seeks.Count != 0, "source end to loop");
        Assert.Equal(TimeSpan.FromSeconds(80), players[0].Seeks.Last());
        Assert.True(page.Preview.IsPlaying);
        Finish(page.PreviewAsync());
        players[1].Seeks.Clear(); players[1].Position = TimeSpan.FromSeconds(10); players[1].RaiseEnded();
        Sta.PumpUntil(() => players[1].Seeks.Count != 0, "rendered selection end to loop");
        Assert.Equal(TimeSpan.Zero, players[1].Seeks.Last());
        page.Tick();
        Assert.Equal(80, page.Playhead);
        Assert.Contains("Rendered selection", page.PreviewLabel);
        page.BeginRangeDrag(); page.SetRange(82, 86);
        Assert.True(players[1].Disposed);
        Assert.True(page.Preview!.IsPlaying);
        players[0].Position = TimeSpan.FromSeconds(86); page.Tick();
        Assert.Equal(82, page.Playhead);
        page.EndRangeDrag();
        page.PauseAndCancel();
        players[0].Seeks.Clear(); players[0].RaiseEnded(); Sta.Pump(TimeSpan.FromMilliseconds(20));
        Assert.False(page.Preview.IsPlaying);
        Assert.Empty(players[0].Seeks);
        // The shared player stays opt-in: ordinary Library playback still stops at EOF.
        var ordinary = new FakePlayer();
        using var ordinaryPage = new PlayerPage(Source.Path, "ordinary", ordinary);
        Finish(ordinaryPage.OpenAsync()); ordinaryPage.PlayPauseCommand.Execute(null); ordinary.RaiseEnded();
        Sta.PumpUntil(() => !ordinaryPage.IsPlaying, "ordinary player to stop at end");
        Assert.Empty(ordinary.Seeks);
    });

    [Fact]
    public async Task FilmstripIsPreparedOnceAndSeekingNeverChangesTrim()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(15);
        await page.EnsureThumbnailsAsync();
        Assert.Equal(2, page.Thumbnails.Length);
        Assert.All(page.Thumbnails, frame => Assert.True(frame.Image.IsFrozen));
        var prepared = _media.Requests.Single(r => r.Operation == "prepareThumbnails");
        Assert.False(Directory.Exists(prepared.Destination));
        var images = page.Thumbnails;
        var range = (page.Start, page.End);
        page.SkipForwardCommand.Execute(null);
        Assert.Equal(25, page.Playhead);
        Assert.Equal(range, (page.Start, page.End));
        page.SkipBackCommand.Execute(null);
        Assert.Equal(15, page.Playhead);
        page.Seek(0); page.SkipBackCommand.Execute(null); Assert.Equal(0, page.Playhead);
        page.Seek(page.Duration); page.SkipForwardCommand.Execute(null); Assert.Equal(page.Duration, page.Playhead);
        page.Zoom(0.5); page.Pan(-0.5); page.Nudge(false, 1); page.Format = "mp3";
        await page.RefreshAsync();
        await page.EnsureThumbnailsAsync();
        Assert.Same(images, page.Thumbnails);
        Assert.Single(_media.Requests, r => r.Operation == "prepareThumbnails");
        await Assert.ThrowsAsync<IOException>(() => ClipThumbnailCache.LoadAsync(_folder,
            new(1, Source, page.Duration, [new(0, "../outside.jpg")]), Source, page.Duration, CancellationToken.None));
    }

    [Fact]
    public async Task OptionalThumbnailWorkCancelsBeforeExportAndRetriesAfterResume()
    {
        _media.ThumbnailRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(0);
        var preparation = page.EnsureThumbnailsAsync();
        var scratch = _media.Requests.Single(r => r.Operation == "prepareThumbnails").Destination!;
        page.SetRange(1, 2);
        await page.RefreshAsync();
        Assert.True(page.CanExport); // Range queries must remain usable during thumbnail preparation.
        await page.ExportAsync(Path.Combine(_folder, "output.mp4"));
        Assert.Single(_media.Requests, r => r.Operation == "export");
        await preparation;
        Assert.False(Directory.Exists(scratch));
        var retry = page.EnsureThumbnailsAsync();
        page.PauseAndCancel();
        await retry;
        Assert.Empty(page.Thumbnails);
        _media.ThumbnailRelease.SetResult();
        page.Resume();
        await page.EnsureThumbnailsAsync();
        Assert.Equal(2, page.Thumbnails.Length);
    }

    [Fact]
    public async Task CancellingWhileThumbnailWorkerStopsCannotStartAnExport()
    {
        _media.ThumbnailRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _media.ThumbnailStopRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(0);
        var preparation = page.EnsureThumbnailsAsync();
        var export = page.ExportAsync(Path.Combine(_folder, "cancelled.mp4"));
        Assert.True(page.Exporting);
        page.CancelCommand.Execute(null);
        _media.ThumbnailStopRelease.SetResult();
        await export;
        await preparation;
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "export");
        _media.ThumbnailRelease.SetResult();
        await page.EnsureThumbnailsAsync();
    }

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
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.DoesNotContain(_media.Requests, r => r.Operation == "waveform");
        var waveform = page.Waveforms[0];
        Assert.Equal(15, waveform.End - waveform.Start, 6);
        Assert.True(waveform.Peaks.Length <= 1600);
        var previous = page.Waveforms;
        page.Seek(41.124);
        page.Zoom(0.0001);
        Assert.Same(previous, page.Waveforms);
        await page.RefreshAsync();
        Assert.InRange(page.Waveforms[0].Peaks.Length, 10, 11);
        Assert.Contains(1f, page.Waveforms[0].Peaks);
        Assert.Contains(0f, page.Waveforms[0].Peaks);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.Equal(page.Start, _media.Requests.Last(r => r.Operation == "range").Start);
        page.SetRange(44, 42);
        Assert.Equal(41.124, page.Start, 6);
        Assert.NotEmpty(page.Status);
        page.SetRange(page.Start, page.End);
        Assert.Empty(page.Status);
    }

    [Fact]
    public async Task WaveformDisplayUsesEachWholeTrackMaximumWithoutChangingCachedAudioPeaks()
    {
        _media.TrackCount = 3;
        _media.PeakAt = (track, index) => track == 3 ? 0 :
            index == 1000 ? (track == 1 ? 0.2f : 0.8f) :
            index == 45000 ? (track == 1 ? 0.1f : 0.4f) : 0;
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(0);
        page.Zoom(3); await page.RefreshAsync();
        var folder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
        var files = Directory.GetFiles(folder).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(new[] { 1f, 1f, 0f }, page.Waveforms.Select(w => w.Peaks.Max()).ToArray());
        page.Seek(45); page.Zoom(0.001);
        await page.RefreshAsync();
        Assert.Equal(new[] { 0.5f, 0.5f, 0f }, page.Waveforms.Select(w => w.Peaks.Max()).ToArray());
        Assert.All(page.Waveforms.SelectMany(w => w.Peaks), peak => Assert.True(float.IsFinite(peak)));
        page.Pan(0.1); await page.RefreshAsync();
        Assert.Equal(new[] { 0.5f, 0.5f, 0f }, page.Waveforms.Select(w => w.Peaks.Max()).ToArray());
        page.Tracks[0].Included = false; await page.RefreshAsync();
        Assert.Equal(0.5f, page.Waveforms.Single(w => w.StreamIndex == 2).Peaks.Max());
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.DoesNotContain(_media.Requests, r => r.Operation is "waveform" or "preview" or "export");
        foreach (var (path, bytes) in files) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Fact]
    public async Task SelectingTheVisibleRangeKeepsViewportPeaksAndUsesInwardMillisecondBounds()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        page.SelectVisibleRange(); Assert.Equal((0d, 0d), (page.Start, page.End));
        await page.OpenAsync(45);
        var sourcePlayer = page.Preview;
        await page.PreviewAsync();
        Assert.NotSame(sourcePlayer, page.Preview);
        page.Zoom(0.5); page.Pan(0.12345); await page.RefreshAsync();
        var viewport = (page.ViewportStart, page.ViewportEnd);
        var peaks = page.Waveforms;
        var playhead = page.Playhead;
        page.SelectVisibleRange(); await page.RefreshAsync();
        Assert.Equal((39.352, 54.351), (page.Start, page.End));
        Assert.Equal("00:00:14.999", page.SelectedDurationText);
        Assert.Equal(viewport, (page.ViewportStart, page.ViewportEnd));
        Assert.Equal(playhead, page.Playhead);
        Assert.Same(peaks, page.Waveforms);
        Assert.Same(sourcePlayer, page.Preview);
        Assert.Equal((page.Start, page.End), (_media.Requests[^1].Start, _media.Requests[^1].End));
        Assert.Equal([1, 2], _media.Requests[^1].Tracks!);
        page.Pan(-100); page.SelectVisibleRange(); Assert.Equal((0d, 15d), (page.Start, page.End));
        page.Pan(100); page.SelectVisibleRange(); Assert.Equal((75d, 90d), (page.Start, page.End));
        page.Zoom(10); page.SelectVisibleRange(); Assert.Equal((0d, 90d), (page.Start, page.End));
        page.SetRange(1, 2); page.PauseAndCancel(); page.SelectVisibleRange();
        Assert.Equal((1d, 2d), (page.Start, page.End));
        page.Resume(); await page.RefreshAsync();
        _media.ExportRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var export = page.ExportAsync(Path.Combine(_folder, "clip.mp4"));
        try { page.SelectVisibleRange(); Assert.Equal((1d, 2d), (page.Start, page.End)); }
        finally { _media.ExportRelease.SetResult(); await export; }
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        page.Dispose(); page.SelectVisibleRange(); Assert.Equal((1d, 2d), (page.Start, page.End));
        using var tiny = new ClipCreatorPage(Source, "tiny", new FakeMedia { Duration = 0.0004 }, () => new FakePlayer());
        await tiny.OpenAsync(0); tiny.SelectVisibleRange();
        Assert.Equal((0d, 0.0004), (tiny.Start, tiny.End));
    }

    [Fact]
    public async Task TrimDragUpdatesSelectionWithoutStartingWorkUntilRelease()
    {
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        await page.OpenAsync(0);
        var peaks = page.Waveforms;
        var rangeCount = _media.Requests.Count(r => r.Operation == "range");
        var viewportNotifications = 0;
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(page.ViewportStart) or nameof(page.ViewportEnd)) { viewportNotifications++; }
        };
        page.BeginRangeDrag();
        for (var i = 1; i <= 200; i++)
        {
            page.SetRange(i / 100d, 30 - i / 100d);
            await page.RefreshAsync();
        }
        Assert.Equal((2d, 28d), (page.Start, page.End));
        Assert.Equal("00:00:26.000", page.SelectedDurationText);
        Assert.Equal(rangeCount, _media.Requests.Count(r => r.Operation == "range"));
        Assert.Equal(0, viewportNotifications);
        Assert.Same(peaks, page.Waveforms);
        Assert.False(page.Busy);
        Assert.False(page.CanExport);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(page.CanExport) && page.CanExport) { released.TrySetResult(); }
        };
        page.EndRangeDrag(); await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(rangeCount + 1, _media.Requests.Count(r => r.Operation == "range"));
        Assert.Equal((2d, 28d), (_media.Requests[^1].Start, _media.Requests[^1].End));
        Assert.Same(peaks, page.Waveforms);
        Assert.True(page.CanExport);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.Empty(page.Status);
        page.BeginRangeDrag(); page.SetRange(3, 27); page.PauseAndCancel();
        await page.RefreshAsync();
        Assert.Equal(rangeCount + 1, _media.Requests.Count(r => r.Operation == "range"));
        page.Resume(); await page.RefreshAsync();
        Assert.True(page.CanExport);
        Assert.Equal((3d, 27d), (_media.Requests[^1].Start, _media.Requests[^1].End));
    }

    [Fact]
    public async Task PreparationSurvivesOrdinaryEditsAndCancelledPreparationCanResume()
    {
        _media.PreparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
        var opening = page.OpenAsync(0);
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        page.SetRange(1.123, 2.456);
        page.Zoom(0.5); page.Pan(0.2); page.Format = "mp3"; page.Tracks[1].Included = false;
        await page.RefreshAsync();
        Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
        Assert.Empty(page.Status);
        page.PauseAndCancel();
        await opening;
        var firstFolder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
        Assert.False(Directory.Exists(firstFolder));
        _media.PreparationRelease.SetResult();
        page.Resume();
        await page.RefreshAsync();
        await page.EnsureWaveformsAsync();
        Assert.Equal(2, _media.Requests.Count(r => r.Operation == "prepareWaveforms"));
        Assert.Equal(2, page.Tracks.Count);
        Assert.Equal((1.123, 2.456), (page.Start, page.End));
        Assert.Single(page.Waveforms);
        Assert.Equal(1, page.Waveforms[0].StreamIndex);
        var peaks = page.Waveforms;
        page.PauseAndCancel(); page.Resume(); await page.RefreshAsync();
        Assert.Equal(2, _media.Requests.Count(r => r.Operation == "prepareWaveforms"));
        Assert.NotEmpty(peaks);
    }

    [Fact]
    public async Task CorruptPreparationIsRejectedAndDisposeWaitsForOwnedWorkerCleanup()
    {
        _media.CorruptPreparation = true;
        using (var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer(),
            createPreviewFolder: () => throw new IOException("Newer preview failure.")))
        {
            await page.OpenAsync(0);
            Assert.Empty(page.Waveforms);
            Assert.Contains("Waveform preparation failed", page.Status);
            var preparationError = page.Status;
            page.SetRange(2, 1);
            Assert.Contains("non-empty range", page.Status);
            page.SetRange(page.Start, page.End);
            Assert.Equal(preparationError, page.Status);
            page.StartText = "invalid";
            await page.PreviewAsync();
            Assert.Equal("Newer preview failure.", page.Status);
            page.SetRange(page.Start, page.End);
            Assert.Equal("Newer preview failure.", page.Status);
            var folder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Source.Path));
        }
        _media.Requests.Clear();
        _media.CorruptPreparation = false;
        _media.CorruptSummary = true;
        using (var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer()))
        {
            await page.OpenAsync(0);
            Assert.Empty(page.Waveforms);
            Assert.Contains("Waveform preparation failed", page.Status);
        }
        _media.Requests.Clear();
        _media.CorruptSummary = false;
        using (var page = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer()))
        {
            await page.OpenAsync(0);
            var previous = page.Waveforms;
            var folder = _media.Requests.Single(r => r.Operation == "prepareWaveforms").Destination!;
            page.PauseAndCancel();
            File.WriteAllBytes(Path.Combine(folder, "track-1-0.rbpk"), [0]);
            page.Zoom(0.0001); await page.RefreshAsync();
            Assert.Same(previous, page.Waveforms);
            Assert.Empty(page.Status);
            page.Resume(); await page.RefreshAsync();
            Assert.Single(_media.Requests, r => r.Operation == "prepareWaveforms");
            Assert.Contains("Cannot read waveform cache", page.Status);
            var readError = page.Status;
            page.EndText = "invalid";
            Assert.Contains("Enter end", page.Status);
            page.SetRange(1, 2);
            Assert.Equal(readError, page.Status);
        }
        _media.Requests.Clear();
        _media.PreparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new ClipCreatorPage(Source, "source", _media, () => new FakePlayer());
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
        using var page = new ClipCreatorPage(Source, "source", _media, () =>
        {
            var player = new FakePlayer { Media = new(1280, 720, TimeSpan.FromSeconds(90), ["Game", "Mic"], true) };
            players.Add(player); return player;
        });
        await page.OpenAsync(0);
        page.SetRange(1, 4); await page.RefreshAsync(); await page.PreviewAsync();
        page.BeginScrub(2.123);
        Assert.Equal(1.123, players[1].Seeks.Last().TotalSeconds, 6);
        players[1].Position = TimeSpan.Zero;
        page.Tick();
        Assert.Equal(2.123, page.Playhead, 6);
        page.Seek(5.678);
        Assert.Equal("Source video", page.PreviewLabel);
        Assert.True(players[1].Disposed);
        Assert.Equal(5.678, players[0].Seeks.Last().TotalSeconds, 6);
        page.EndScrub();
        Assert.False(page.IsScrubbing);
        Assert.Equal((1d, 4d), (page.Start, page.End));
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
                Assert.Equal(28 + 5 * 40 + 48, waveform.ActualHeight);
                Assert.Equal(5, waveform.ScrollMaximum);
                waveform.LaneOffset = 5;
                Assert.Equal(5, waveform.LaneOffset);
                Assert.Equal(((SolidColorBrush)window.FindResource("AccentFillColorDefaultBrush")).Color, ((SolidColorBrush)waveform.WaveBrush).Color);
                Assert.NotEqual(((SolidColorBrush)waveform.SurfaceBrush).Color, ((SolidColorBrush)waveform.RulerBrush).Color);
                Assert.Contains(Descendants<TextBox>(view), t => t.Text == page.StartText);
                Assert.Empty(Descendants<CheckBox>(view));
                Assert.True(((PlayerView)view.FindName("Video")).ActualHeight > 320);
                CommandManager.InvalidateRequerySuggested(); Sta.Pump(TimeSpan.FromMilliseconds(50));
                Assert.True(((Button)view.FindName("PreviewButton")).IsEnabled, $"Preview command {page.CanExport}, busy {page.Busy}, status {page.Status}");
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
                var originalRange = (page.Start, page.End);
                var originalPlayhead = page.Playhead;
                waveform.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.P) { RoutedEvent = Keyboard.KeyDownEvent });
                waveform.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.Equal(originalPlayhead + 0.001, page.Playhead, 6);
                Assert.Equal(originalRange, (page.Start, page.End));
                page.BeginScrub(3);
                waveform.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Assert.False(page.IsScrubbing);
                Assert.Equal(originalRange, (page.Start, page.End));
                page.BeginRangeDrag(); waveform.MoveBoundary(false, 0.5);
                Assert.False(page.CanExport);
                waveform.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Sta.PumpUntil(() => page.CanExport, "trim release to resolve its final range");
                Assert.True(page.CanExport);
                page.Zoom(0.5);
                page.SetRange(2, 10);
                Assert.False(waveform.TrySelectVisibleRange(new Point(50, 40), 2));
                Assert.False(waveform.TrySelectVisibleRange(new Point(200, 10), 2));
                Assert.False(waveform.TrySelectVisibleRange(new Point(200, 40), 1));
                Assert.False(waveform.TrySelectVisibleRange(new Point(waveform.ActualWidth + 1, 40), 2));
                Assert.Equal((2d, 10d), (page.Start, page.End));
                var handleX = 100 + (page.Start - page.ViewportStart) / (page.ViewportEnd - page.ViewportStart) * (waveform.ActualWidth - 100);
                page.BeginScrub(3); page.BeginRangeDrag();
                Assert.True(waveform.TrySelectVisibleRange(new Point(handleX, 40), 2));
                Assert.False(page.IsScrubbing);
                Assert.False(waveform.IsMouseCaptured);
                Assert.Equal(page.ViewportStart, page.Start, 6);
                Assert.Equal(page.ViewportEnd, page.End, 6);
                waveform.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Sta.PumpUntil(() => page.CanExport, "double-click selection to resolve");
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
                    window.Height += 300; window.UpdateLayout();
                    Assert.True(video.ActualHeight >= originalHeight + 299);
                    Assert.Equal(0, scroll.ScrollableHeight);
                    window.Height -= 300; window.UpdateLayout();
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
                    Enumerable.Range(1, TrackCount).Select(i => new ClipAudioTrack(i, i == 1 ? "Game" : i == 2 ? "Mic" : $"Track {i}", 48000, 2)).ToArray())),
                "range" => new ClipMediaResponse("complete", Range: new(request.Start, request.End, request.Format == "mp3" ? "audioSamples48000" : "videoFrames")),
                "waveform" => new ClipMediaResponse("complete", Waveforms: (request.Tracks ?? []).Select(i => new ClipWaveform(request.Start, request.End, i, new float[request.Bins])).ToArray()),
                _ => Render(request),
            };
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
