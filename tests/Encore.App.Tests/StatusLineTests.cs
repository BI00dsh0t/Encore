using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Engine;
using Encore.App.Status;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

public sealed class StatusLineTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void TheHeaderShowsOnlyErrorsAndServiceFailuresAndCollapsesAfterRecovery()
    {
        var line = new StatusLine(_engine);
        var routine = Snapshot(Warning("info", Ten, "Saved.", WarningSeverity.Info), Warning("warning", Ten, "Mic muted."));
        line.Render(EngineStatus.Connected, null, routine);
        Assert.False(line.HasErrors);
        Assert.Empty(line.Errors);
        Assert.Equal(2, routine.Warnings.Count);
        var error = Warning("error", Ten.AddMinutes(1), "The disk is full.", WarningSeverity.Error);
        line.Render(EngineStatus.Connected, null, routine with { Warnings = [.. routine.Warnings, error] });
        Assert.True(line.HasErrors);
        Assert.Equal("error", Assert.Single(line.Errors).Id);
        line.Render(EngineStatus.Connected, null, routine);
        Assert.False(line.HasErrors);
        line.Render(EngineStatus.NotRunning, "Service failed.", null);
        Assert.True(line.HasErrors);
        Assert.Contains("Service failed.", line.ConnectionError);
        line.Render(EngineStatus.Connecting, null, null);
        Assert.False(line.HasErrors);
        line.Render(EngineStatus.Connected, null, routine);
        Assert.Empty(line.ConnectionError);
        Assert.False(line.HasErrors);
        Assert.Empty(_engine.Sent);
    }

    [Fact]
    public void EachCaptureStateShowsItsText()
    {
        var line = new StatusLine(_engine);

        Assert.Equal("Waiting for a game", Render(line, CaptureState.Initial with { MicMuted = false }));
        Assert.Equal(
            "Ready to save Clips in Game.exe · Mic muted",
            Render(line, CaptureState.Initial with { Replay = ReplayState.Buffering }, new TargetGame("p1", "Game.exe", 77)));
        Assert.Equal(
            "Finishing the save… · Recording · Monitor Recording",
            Render(line, new CaptureState(ReplayState.Draining, RecordingState.Running, RecordingState.Running, MicMuted: false, CooldownUntil: null)));
        Assert.Equal("Ready to save Clips", Render(line, CaptureState.Initial with { Replay = ReplayState.Buffering, MicMuted = false }));
    }

    [Fact]
    public void TheDotIsRecordWhileRecordingSuccessWhileBufferingAndIdleOtherwise()
    {
        var line = new StatusLine(_engine);

        Render(line, CaptureState.Initial with { Replay = ReplayState.Buffering });
        Assert.Equal(StatusDot.Buffering, line.Dot);
        Render(line, CaptureState.Initial with { Replay = ReplayState.Buffering, MonitorRecording = RecordingState.Running });
        Assert.Equal(StatusDot.Recording, line.Dot);
        Render(line, CaptureState.Initial);
        Assert.Equal(StatusDot.Idle, line.Dot);

        line.Render(EngineStatus.Restarting, null, null);
        Assert.Equal(StatusDot.Idle, line.Dot);
    }

    [Fact]
    public void WarningsListNewestFirstWithTheirTimeAndAReconnectReplacesThem()
    {
        var line = new StatusLine(_engine);
        line.Render(EngineStatus.Connected, null, Snapshot(Warning("old", Ten, "First."), Warning("new", Ten.AddMinutes(5), "Second.", dismissable: false)));

        Assert.Equal(["new", "old"], line.Warnings.Select(w => w.Id));
        Assert.Equal(Ten.AddMinutes(5).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), line.Warnings[0].Time);
        Assert.Equal(("Second.", "Warning", false), (line.Warnings[0].Message, line.Warnings[0].Severity, line.Warnings[0].CanDismiss));

        line.Render(EngineStatus.Connected, null, Snapshot(Warning("restart", Ten.AddMinutes(9), "Engine restarted.")));

        Assert.Equal(["restart"], line.Warnings.Select(w => w.Id));
    }

    [Fact]
    public void DismissSendsTheIdAndTheRowStaysUntilTheSnapshotDropsIt()
    {
        var line = new StatusLine(_engine);
        var snapshot = Snapshot(Warning("w1", Ten, "Device dropped."));
        line.Render(EngineStatus.Connected, null, snapshot);
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.EngineDisconnected, "Gone.");

        line.Warnings[0].DismissCommand.Execute(null);

        Assert.Equal("w1", Assert.IsType<WarningDismiss>(Assert.Single(_engine.Sent)).Id);
        Assert.Single(line.Warnings);
        line.Render(EngineStatus.Connected, null, snapshot with { Warnings = [] });
        Assert.Empty(line.Warnings);
    }

    private string Render(StatusLine line, CaptureState capture, TargetGame? game = null)
    {
        line.Render(EngineStatus.Connected, null, _engine.State! with { Capture = capture, TargetGame = game });
        return line.State;
    }

    private StateSnapshot Snapshot(params Warning[] warnings) => _engine.State! with { Warnings = warnings };

    internal static Warning Warning(string id, DateTimeOffset at, string message, WarningSeverity severity = WarningSeverity.Warning, bool dismissable = true) =>
        new(id, ErrorCodes.SettingsUnreadable, severity, "test", message, at, Overlay: false, Sound: null, dismissable);
}

/// <summary>The status line's Explicit PNG render: it builds a WPF window, so it takes its turn with the other WPF tests.</summary>
[Collection(nameof(WpfCollection))]
public sealed class StatusLineRenderTests
{
    private readonly FakeEngineSession _engine = new();

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TheStatusLineRendersAboveThePageToAPng() => Sta.Run(() =>
    {
        _engine.State = Snapshot(
            Warning("w1", DateTimeOffset.Now.AddMinutes(-3), "The microphone dropped out; its Track records silence until it returns."),
            Warning("w2", DateTimeOffset.Now.AddMinutes(-1), "The engine restarted after a crash.", WarningSeverity.Error),
            Warning("w3", DateTimeOffset.Now, "Free memory is close to Rewind's size.", dismissable: false)) with
        {
            Capture = CaptureState.Initial with { Replay = ReplayState.Buffering, Recording = RecordingState.Running },
            TargetGame = new TargetGame("p1", "Game.exe", 77),
        };
        var window = new MainWindow
        {
            DataContext = new MainViewModel(_engine, action => action()),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var context = background.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }

            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Environment.GetEnvironmentVariable("RB_SCREENSHOT") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Path.GetTempPath(), "status-line.png");
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    private StateSnapshot Snapshot(params Warning[] warnings) => _engine.State! with { Warnings = warnings };

    private static Warning Warning(string id, DateTimeOffset at, string message, WarningSeverity severity = WarningSeverity.Warning, bool dismissable = true) =>
        StatusLineTests.Warning(id, at, message, severity, dismissable);

    [Fact]
    public void TheHintSaysWhichKeySavesHowMuchWhileCapturingOnly()
    {
        // Story 10.6: hidden during a Recording and when the Save Hotkey failed to register.
        var engine = new FakeEngineSession();
        var line = new StatusLine(engine);
        var settings = Contracts.Settings.SettingsDocument.Empty with
        {
            Global = Contracts.Settings.SettingsMaps.EmptyValues
                .Add(Contracts.Settings.Keys.BufferWindow.Name, Contracts.Settings.Keys.BufferWindow.ToJson(30))
                .Add(Contracts.Settings.Keys.Hotkeys.Name, Contracts.Settings.Keys.Hotkeys.ToJson(new Contracts.Settings.HotkeySettings(new Contracts.Settings.HotkeyBinding(0x79, Contracts.Settings.HotkeyModifiers.Alt), null, null, null, null, null))),
        };
        var buffering = engine.State! with { Settings = settings, Capture = CaptureState.Initial with { Replay = ReplayState.Buffering, MicMuted = false } };

        line.Render(EngineStatus.Connected, null, buffering);
        Assert.Equal("· Alt+F10 saves the last 30 s", line.Hint);

        line.Render(EngineStatus.Connected, null, buffering with { Capture = buffering.Capture with { Recording = RecordingState.Running } });
        Assert.Equal("", line.Hint);

        var failed = new HotkeyStatus([new HotkeyBindingStatus(Contracts.Settings.HotkeyAction.Save, new Contracts.Settings.HotkeyBinding(0x79, Contracts.Settings.HotkeyModifiers.Alt), false, Contracts.ErrorCodes.HotkeyTaken)]);
        line.Render(EngineStatus.Connected, null, buffering with { Hotkeys = failed });
        Assert.Equal("", line.Hint);

        line.Render(EngineStatus.Connected, null, buffering with { Capture = CaptureState.Initial });
        Assert.Equal("", line.Hint);
    }
}
