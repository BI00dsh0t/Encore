using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Settings;
using Encore.App.Theme;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The Hotkeys screen picker, on a fabricated monitor list.</summary>
[Collection(nameof(WpfCollection))]
public sealed class MonitorPickerTests
{
    // The builder's two screens and a portable one, left of the primary and lower.
    private static readonly DisplayMonitor Dell = new(@"\\.\DISPLAY1", "DELL", 2560, 1440, true, "path-dell", 0, 0, 240);
    private static readonly DisplayMonitor Lg = new(@"\\.\DISPLAY2", "LG", 3840, 2160, false, "path-lg", 2560, 0, 144, Hdr: true);
    private static readonly DisplayMonitor Portable = new(@"\\.\DISPLAY3", "Portable", 1920, 1080, false, "path-portable", -1920, 600, 60);

    private readonly FakeEngineSession _engine = new();
    private readonly List<SettingsUpdate> _updates = [];

    public MonitorPickerTests()
    {
        _engine.Monitors = () => new MonitorList([Dell, Lg, Portable]);
        _engine.Reply = (_, args) =>
        {
            var update = (SettingsUpdate)args;
            _updates.Add(update);
            return new SettingsUpdateResult(update.ExpectedRevision + 1);
        };
    }

    [Fact]
    public void AllScreensAndATickedSetSendTheKeyAndTheLastTickedScreenCantBeUnticked()
    {
        SetState(revision: 3, screens: null);
        var picker = Create().Hotkeys.Monitor;

        // An unset key shows the screen the old setting records: the primary.
        Assert.True(picker.IsChosenScreens);
        Assert.Equal(["1  DELL · 2560 × 1440 · 240 Hz · primary", "2  LG · 3840 × 2160 · 144 Hz · HDR, recorded in SDR", "3  Portable · 1920 × 1080 · 60 Hz"], picker.Rows.Select(r => r.Label));
        Assert.Equal([true, false, false], picker.Rows.Select(r => r.IsChosen));

        picker.Rows[1].IsChosen = true;
        picker.IsAllScreens = true;
        Assert.All(picker.Rows, r => Assert.True(r.IsChosen && !r.IsEnabled));
        picker.IsChosenScreens = true;

        // The last ticked screen stays ticked, with nothing sent and a tooltip saying why.
        picker.Rows[0].IsChosen = false;
        picker.Rows[1].IsChosen = false;
        Assert.True(picker.Rows[1].IsChosen);
        Assert.NotNull(picker.Rows[1].ToolTip);

        Assert.All(_updates, u => Assert.Equal((SettingsScope.Global, Keys.MonitorRecordingScreens.Name), (Assert.Single(u.Ops).Scope, u.Ops[0].Key)));
        Assert.Equal(
            [
                Json(MonitorRecordingScreens.Selected("path-dell", "path-lg")),
                Json(MonitorRecordingScreens.AllScreens),
                Json(MonitorRecordingScreens.Selected("path-dell", "path-lg")),
                Json(MonitorRecordingScreens.Selected("path-lg")),
            ],
            _updates.Select(u => u.Ops[0].Value.GetRawText()));
    }

    [Fact]
    public void ThePreviewFollowsThePositionsAndTogglesTheSameSelectionAndHidesWithoutPositions()
    {
        SetState(revision: 1, MonitorRecordingScreens.Selected("path-dell"));
        var model = Create();
        var picker = model.Hotkeys.Monitor;
        Assert.True(picker.HasPreview);
        var (dell, lg, portable) = (picker.Tiles[0], picker.Tiles[1], picker.Tiles[2]);

        // Portable sits left of and below the primary; the LG is right of it, top-aligned and 1.5 × wider.
        Assert.True(portable.Left < dell.Left && dell.Left < lg.Left);
        Assert.Equal(dell.Top, lg.Top);
        Assert.True(portable.Top > dell.Top);
        Assert.Equal(1.5, (lg.Width + 4) / (dell.Width + 4), 3);
        Assert.True(picker.PreviewWidth <= MonitorPicker.PreviewMaxWidth && picker.PreviewHeight <= MonitorPicker.PreviewMaxHeight);
        Assert.Equal(["1", "2", "3"], picker.Tiles.Select(t => t.Number));
        Assert.True(dell.Primary);

        // A tile is its row: toggling it ticks the row and sends the selection.
        portable.Row.IsChosen = true;
        Assert.True(picker.Rows[2].IsChosen);
        Assert.Equal(Json(MonitorRecordingScreens.Selected("path-dell", "path-portable")), _updates[^1].Ops[0].Value.GetRawText());

        // An engine that doesn't fill the positions: no preview.
        _engine.Monitors = () => new MonitorList([Dell with { X = null, Y = null }, Lg]);
        picker.RefreshCommand.Execute(null);
        Assert.False(picker.HasPreview);
        Assert.Empty(picker.Tiles);
    }

    [Fact]
    public void TheOutputLineFollowsTheCanvasRule()
    {
        SetState(revision: 1, MonitorRecordingScreens.Selected("path-dell", "path-lg"));
        _engine.State = _engine.State! with { VideoOptions = VideoOptions.FromProbe([new(VideoCodec.H264, "h264_nvenc", false), new(VideoCodec.Hevc, "hevc_nvenc", true)]) };
        var model = Create();
        var picker = model.Hotkeys.Monitor;
        Assert.Equal("Recorded as one 6400 × 2160 video. It's saved as HEVC, so every screen keeps its full detail.", picker.OutputText);

        // Neither HEVC nor AV1: H.264, fitted to 4096 px.
        _engine.State = _engine.State with { VideoOptions = VideoOptions.FromProbe([new(VideoCodec.H264, "h264_nvenc", false)]) };
        model.Refresh();
        Assert.Equal("Recorded as one 4096 × 1382 video (scaled down). HEVC would keep full detail, but your graphics card's video encoder can't make it.", picker.OutputText);

        picker.Rows[1].IsChosen = false;
        Assert.Equal("Recorded as one 2560 × 1440 video.", picker.OutputText);
    }

    [Fact]
    public void AMonitorsChangedRemovingAChosenScreenShowsItNotConnected()
    {
        SetState(revision: 1, MonitorRecordingScreens.Selected("path-dell", "path-lg"));
        var model = Create();
        var picker = model.Hotkeys.Monitor;
        Assert.Equal(3, picker.Rows.Count);

        // The LG is unplugged: the list follows the event, without a fetch.
        _engine.State = RecordingMessages.MonitorsChanged.Apply(_engine.State!, new MonitorList([Dell, Portable]));
        model.Refresh();
        Assert.Equal(1, _engine.MonitorFetches);
        var lg = picker.Rows[^1];
        Assert.Equal(("path-lg", "2  LG · Not connected — skipped", false, true), (lg.Id, lg.Label, lg.IsConnected, lg.IsChosen));
        Assert.Equal(2, picker.Tiles.Count);
        Assert.Equal("Recorded as one 2560 × 1440 video.", picker.OutputText);
        Assert.Empty(_updates);
    }

    [Fact]
    public void TheHotkeysPageRendersThePickerInBothThemes() => Sta.Run(() =>
    {
        SetState(revision: 1, MonitorRecordingScreens.Selected("path-dell", "path-lg", "path-gone"));
        var model = Create();
        var window = new MainWindow
        {
            DataContext = model,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            Height = 900,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        var theme = ThemeService.ForWindow(window);
        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(which);
                window.Show();
                model.SelectedNavItem = model.NavItems.First(i => i.Page == model.Hotkeys);
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                var picker = (FrameworkElement)model.Hotkeys.View.FindName("ScreenPicker");
                picker.BringIntoView();
                Sta.Pump(TimeSpan.FromMilliseconds(100));

                Assert.True(picker.IsVisible, $"The picker isn't shown under {which}");
                Assert.True(((FrameworkElement)model.Hotkeys.View.FindName("ScreenPreview")).IsVisible);
                Assert.Equal(4, Descendants<CheckBox>(picker).Count());
                var tiles = Descendants<System.Windows.Controls.Primitives.ToggleButton>(picker).Where(t => t is not CheckBox and not RadioButton).ToList();
                Assert.Equal(3, tiles.Count(t => t.Focusable));

                // A tile toggles its row's check box through the view's bindings.
                if (which == AppTheme.GamingDark)
                {
                    tiles[2].IsChecked = true;
                    Sta.Pump(TimeSpan.FromMilliseconds(50));
                    Assert.True(Descendants<CheckBox>(picker).ElementAt(2).IsChecked);
                }

                Save(window, $"hotkeys-screens-picker-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
            window.CloseForQuit();
        }

        Assert.Empty(errors.Messages);
    });

    private static string Json(MonitorRecordingScreens screens) => Keys.MonitorRecordingScreens.ToJson(screens).GetRawText();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    // The HITL record's picture of the page, beside the test binary.
    private static void Save(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }

    private void SetState(long revision, MonitorRecordingScreens? screens)
    {
        var global = SettingsDocument.Empty.Global.Add(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(screens));
        _engine.State = _engine.State! with { Settings = SettingsDocument.Empty with { Revision = revision, Global = global } };
    }

    private MainViewModel Create() => new(_engine, action => action());
}
