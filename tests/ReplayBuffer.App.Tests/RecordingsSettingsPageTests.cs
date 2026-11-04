using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class RecordingsSettingsPageTests
{
    private static readonly DisplayMonitor Dell = new(@"\\.\DISPLAY1", "DELL", 2560, 1440, true);
    private static readonly DisplayMonitor Lg = new(@"\\.\DISPLAY2", "LG", 1920, 1080, false);

    private readonly FakeEngineSession _engine = new();
    private readonly List<SettingsUpdate> _updates = [];

    public RecordingsSettingsPageTests()
    {
        _engine.Monitors = () => new MonitorList([Dell, Lg]);
        _engine.Reply = (_, args) =>
        {
            var update = (SettingsUpdate)args;
            _updates.Add(update);
            return new SettingsUpdateResult(update.ExpectedRevision + 1);
        };
    }

    [Fact]
    public void TheListOffersPrimaryThenEachMonitorAndAChoiceSendsOneGlobalOp()
    {
        SetState(revision: 3, monitor: null);
        var model = Create();
        var page = model.Recordings;
        Assert.Equal(["Primary monitor", "DELL — 2560×1440 (primary)", "LG — 1920×1080"], page.Monitors.Select(m => m.Label));
        Assert.Null(page.SelectedMonitor!.DeviceName);

        page.SelectedMonitor = page.Monitors[2];
        page.SelectedMonitor = null;

        var update = Assert.Single(_updates);
        Assert.Equal(3, update.ExpectedRevision);
        var op = Assert.Single(update.Ops);
        Assert.Equal((SettingsScope.Global, Keys.MonitorRecordingMonitor.Name, Keys.MonitorRecordingMonitor.ToJson(@"\\.\DISPLAY2").GetRawText()), (op.Scope, op.Key, op.Value.GetRawText()));

        SetState(revision: 4, monitor: @"\\.\display2");
        model.Refresh();
        Assert.Equal(Lg.DeviceName, page.SelectedMonitor!.DeviceName);
        page.SelectedMonitor = page.Monitors[0];
        Assert.Equal("null", Assert.Single(_updates[^1].Ops).Value.GetRawText());
        Assert.Equal(1, _engine.MonitorFetches);
    }

    [Fact]
    public void AMissingMonitorShowsAsNotConnectedAndAFailedFetchShowsItsError()
    {
        SetState(revision: 1, monitor: @"\\.\DISPLAY3");
        var model = Create();
        var page = model.Recordings;
        Assert.Equal(@"\\.\DISPLAY3 (not connected)", page.Monitors[^1].Label);
        Assert.Same(page.Monitors[^1], page.SelectedMonitor);

        _engine.Monitors = () => throw new ErrorCodeException(ErrorCodes.EngineDisconnected, "Gone.");
        page.RefreshCommand.Execute(null);
        Assert.Equal($"Couldn't list monitors ({ErrorCodes.EngineDisconnected}): Gone.", page.Message);
        Assert.Equal(4, page.Monitors.Count);

        // A new engine run lists them again.
        _engine.Monitors = () => new MonitorList([Dell]);
        _engine.State = _engine.State! with { EngineInstanceId = "run-2" };
        model.Refresh();
        Assert.Equal(3, _engine.MonitorFetches);
        Assert.Equal(3, page.Monitors.Count);
        Assert.Null(page.Message);
    }

    [Fact]
    public void TheSettingsTabListsThePageAndItsViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        SetState(revision: 1, monitor: Lg.DeviceName);
        var model = Create();
        Assert.Contains(model.Pages, p => p.Title == "Recordings");
        model.SelectedPage = model.Recordings;

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedPage.View;
            view.Measure(new Size(600, 300));
            view.Arrange(new Rect(0, 0, 600, 300));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual();
            using (var context = white.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 300));
            }

            bitmap.Render(white);
            bitmap.Render(view);

            // The HITL record's picture of the page.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "recordings-settings.png")))
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

        Assert.DoesNotContain(errors.Messages, m => m.Contains("Recordings", StringComparison.Ordinal) || m.Contains("MonitorItem", StringComparison.Ordinal));
    });

    private void SetState(long revision, string? monitor)
    {
        var global = SettingsDocument.Empty.Global.Add(Keys.MonitorRecordingMonitor.Name, Keys.MonitorRecordingMonitor.ToJson(monitor));
        _engine.State = _engine.State! with { Settings = SettingsDocument.Empty with { Revision = revision, Global = global } };
    }

    private MainViewModel Create() => new(_engine, action => action());
}
