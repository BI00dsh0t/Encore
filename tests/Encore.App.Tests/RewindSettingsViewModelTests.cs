using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class RewindSettingsViewModelTests
{
    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void EachEditSendsOneGlobalOpAtTheRenderedRevisionAndABadBufferWindowSendsNothing()
    {
        _engine.State = _engine.State! with { Settings = SettingsDocument.Empty with { Revision = 3 } };
        var updates = new List<SettingsUpdate>();
        _engine.Reply = (_, args) =>
        {
            updates.Add((SettingsUpdate)args);
            return new SettingsUpdateResult(4);
        };
        var viewModel = Create().Rewind;

        viewModel.BufferWindowText = "0";
        Assert.Empty(updates);
        Assert.Equal("300", viewModel.BufferWindowText);
        Assert.NotNull(viewModel.Message);

        viewModel.BufferWindowText = "120";
        viewModel.IsDisk = true;
        viewModel.BitrateText = "20000";
        viewModel.IsVbr = true;

        var video = Keys.Video.Default with { BitrateKbps = 20000 };
        Assert.All(updates, u => Assert.Equal(3, u.ExpectedRevision));
        Assert.Equal(
            [
                (Keys.BufferWindow.Name, "120"),
                (Keys.ReplayStorage.Name, "\"disk\""),
                (Keys.Video.Name, Keys.Video.ToJson(video).GetRawText()),
                (Keys.Video.Name, Keys.Video.ToJson(video with { RateControl = RateControl.Vbr }).GetRawText()),
            ],
            updates.Select(u => Assert.Single(u.Ops)).Select(o => (o.Key, o.Value.GetRawText())));
        Assert.All(updates, u => Assert.Equal(SettingsScope.Global, u.Ops[0].Scope));
        Assert.Null(viewModel.Message);
    }

    [Fact]
    public void TheEstimateAndItsWarningFollowTheSnapshotAndHevcMayNotPlayEverywhere()
    {
        var model = Create();
        var viewModel = model.Rewind;
        Assert.Equal("Estimate not available yet", viewModel.EstimateText);

        Show(model, new StorageEstimate(ReplayStorage.Memory, 1_600_000_000, WorstCase: false, 32_000_000_000, ExceedsFree: false));
        Assert.Equal("Estimated RAM use: 1.6 GB", viewModel.EstimateText);
        Assert.Null(viewModel.EstimateWarning);

        Show(model, new StorageEstimate(ReplayStorage.Disk, 2_400_000_000, WorstCase: true, 900_000_000, ExceedsFree: true));
        Assert.Equal("Estimated disk use: 2.4 GB (worst case for VBR)", viewModel.EstimateText);
        Assert.Equal("More than the free space on the cache folder's drive (900.0 MB). The value is kept.", viewModel.EstimateWarning);

        Show(model, new StorageEstimate(ReplayStorage.Memory, 2_400_000_000, WorstCase: false, 1_000_000_000, ExceedsFree: true));
        Assert.Equal("More than the free RAM (1.0 GB). The value is kept; Clips may be shorter.", viewModel.EstimateWarning);

        _engine.State = _engine.State! with
        {
            VideoOptions = VideoOptions.FromProbe(
            [
                new CodecOffer(VideoCodec.H264, "h264_nvenc", false),
                new CodecOffer(VideoCodec.Hevc, "hevc_nvenc", true),
            ]),
        };
        model.Refresh();
        Assert.Equal(["H.264 (plays everywhere)", "HEVC (smaller files) — may not play everywhere"], viewModel.Codecs.Select(c => c.Label));
        Assert.Equal(VideoCodec.H264, viewModel.SelectedCodec?.Codec);
        Assert.Contains(viewModel.Resolutions, r => r.Label == "1920 × 1080");
    }

    [Fact]
    public void SourceComesFirstSendsMatchSourceKeepingTheSizeAndAFixedSizeTurnsItOff()
    {
        var updates = new List<SettingsUpdate>();
        _engine.Reply = (_, args) =>
        {
            updates.Add((SettingsUpdate)args);
            return new SettingsUpdateResult(1);
        };
        _engine.State = _engine.State! with
        {
            VideoOptions = VideoOptions.FromProbe([new CodecOffer(VideoCodec.H264, "h264_nvenc", false), new CodecOffer(VideoCodec.Hevc, "hevc_nvenc", true)]),
        };
        var viewModel = Create().Rewind;
        Assert.Equal(Encore.App.Settings.RewindSettingsViewModel.SourceLabel, viewModel.Resolutions[0].Label);
        Assert.Null(viewModel.SourceHelp);

        viewModel.SelectedResolution = viewModel.Resolutions[0];
        Assert.True(viewModel.SelectedResolution!.IsSource);
        Assert.Contains("4096 px", viewModel.SourceHelp, StringComparison.Ordinal);
        viewModel.SelectedCodec = viewModel.Codecs.Single(c => c.Codec == VideoCodec.Hevc);
        Assert.DoesNotContain("4096", viewModel.SourceHelp, StringComparison.Ordinal);
        viewModel.SelectedResolution = viewModel.Resolutions.Single(r => r.Label == "1280 × 720");
        Assert.Null(viewModel.SourceHelp);

        Assert.Equal(
            [
                Keys.Video.Default with { MatchSource = true },
                Keys.Video.Default with { MatchSource = true, Codec = VideoCodec.Hevc },
                Keys.Video.Default with { Codec = VideoCodec.Hevc, Width = 1280, Height = 720 },
            ],
            updates.Select(u => Keys.Video.TryRead(Assert.Single(u.Ops).Value, out var v) ? v : null));
    }

    [Fact]
    public void TheCacheFolderIsEnabledOnlyUnderDiskAndSendsRingPathAndTimeBetweenSaves()
    {
        // The ring path is the cache folder's picker; the Clips folder is D:\Clips.
        _engine.State = _engine.State! with
        {
            Settings = SettingsDocument.Empty with { Revision = 2, Global = SettingsMaps.EmptyValues.Add(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(@"D:\Clips")) },
        };
        var updates = new List<SettingsUpdate>();
        _engine.Reply = (_, args) =>
        {
            updates.Add((SettingsUpdate)args);
            return new SettingsUpdateResult(3);
        };
        var viewModel = Create().Rewind;
        Assert.Equal("Clips", viewModel.Title);
        Assert.False(viewModel.CacheFolder.IsEnabled);
        Assert.Equal("Used only in Disk mode", viewModel.CacheFolder.Caption);

        viewModel.IsDisk = true;
        Assert.True(viewModel.CacheFolder.IsEnabled);

        viewModel.CacheFolder.Dialog = _ => @"D:\Clips\cache";
        viewModel.CacheFolder.BrowseCommand.Execute(null);
        Assert.Equal("The cache folder can't be inside the Clips folder.", viewModel.CacheFolder.Problem);

        var cache = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        viewModel.CacheFolder.Dialog = _ => cache;
        viewModel.CacheFolder.BrowseCommand.Execute(null);
        viewModel.SaveCooldownText = "7";

        Assert.Equal(
            [(Keys.ReplayStorage.Name, "\"disk\""), (Keys.RingPath.Name, Keys.RingPath.ToJson(cache).GetRawText()), (Keys.SaveCooldown.Name, "7")],
            updates.Select(u => Assert.Single(u.Ops)).Select(o => (o.Key, o.Value.GetRawText())));
    }

    [Fact]
    public void TheViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        _engine.State = _engine.State! with
        {
            VideoOptions = VideoOptions.FromProbe([new CodecOffer(VideoCodec.H264, "h264_nvenc", false), new CodecOffer(VideoCodec.Av1, "av1_nvenc", true)]),
            Estimate = new StorageEstimate(ReplayStorage.Memory, 2_400_000_000, WorstCase: true, 1_000_000_000, ExceedsFree: true),
        };
        var model = Create();
        model.SelectedSettingsViewModel = model.Rewind;

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedSettingsViewModel.View;
            view.Measure(new Size(600, 500));
            view.Arrange(new Rect(0, 0, 600, 500));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual();
            using (var context = white.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 500));
            }

            bitmap.Render(white);
            bitmap.Render(view);

            // The HITL record's picture of the page.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "replay-buffer-settings.png")))
            {
                encoder.Save(file);
            }

            Assert.True(view.ActualHeight > 0);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        Assert.DoesNotContain(errors.Messages, m => m.Contains("RewindSettings", StringComparison.Ordinal) || m.Contains("CodecItem", StringComparison.Ordinal) || m.Contains("ResolutionItem", StringComparison.Ordinal));
    });

    private void Show(MainViewModel model, StorageEstimate estimate)
    {
        _engine.State = _engine.State! with { Estimate = estimate };
        model.Refresh();
    }

    private MainViewModel Create() => new(_engine, action => action());
}
