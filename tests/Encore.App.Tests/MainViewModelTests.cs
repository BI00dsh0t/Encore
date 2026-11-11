using System.Text.Json;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

public sealed class MainViewModelTests
{
    private readonly FakeEngineSession _engine = new();
    private readonly List<Action> _posted = [];

    [Fact]
    public void TheMainNavHoldsTheLibraryAndSettingsListsTheSevenPagesInOrder()
    {
        // The builder's approved order; Games has its own title.
        var model = Create();

        Assert.Equal(["Library"], model.NavItems.Select(i => i.Title));
        Assert.Equal(
            ["General", "Clips", "Hotkeys", "Audio Tracks", "Games", "Sounds", "Storage and Cleanup"],
            model.Settings.Sections.Select(i => i.Title));
        Assert.DoesNotContain(model.Settings.Sections, i => i.Title == "Recordings");
    }

    [Fact]
    public void EscapeAndBackCloseAnOpenItemPageAndTheSettingsButtonAsksForTheWindow()
    {
        var model = Create();
        var asked = 0;
        model.Settings.OpenRequested += () => asked++;
        Assert.False(model.HandleEscape());
        Assert.False(model.GoBack());

        model.OpenSettings();
        Assert.Equal(1, asked);
        Assert.Same(model.General, model.Settings.SelectedPage);
        model.Settings.Select(model.GameProfiles);
        var drillIn = model.Settings.CurrentDrillIn!;
        drillIn.Open("game", "Game");
        Assert.True(model.Settings.GoBack());
        Assert.False(drillIn.IsOpen);
        Assert.False(model.Settings.GoBack());

        // Choosing another section closes the item page of the one left; Ctrl+Tab steps round.
        drillIn.Open("game", "Game");
        model.Settings.StepBy(1);
        Assert.False(drillIn.IsOpen);
        Assert.Same(model.FeedbackSounds, model.Settings.SelectedPage);
        model.Settings.Select(model.General);
        model.Settings.StepBy(-1);
        Assert.Same(model.LibrarySettings, model.Settings.SelectedPage);
    }

    [Fact]
    public void TheNavPaneIsFullNarrowInASmallWindowAndARailWhenCollapsed()
    {
        Assert.Equal(200, NavPaneLayout.WidthFor(expanded: true, windowWidth: 960));
        Assert.Equal(160, NavPaneLayout.WidthFor(expanded: true, windowWidth: 600));
        Assert.Equal(58, NavPaneLayout.WidthFor(expanded: false, windowWidth: 960));
    }

    [Fact]
    public void ARefreshRendersTheSnapshotAndTheSettingsPages()
    {
        var model = Create();
        Assert.Equal("Waiting for a game · Mic muted", model.StatusLine.State);
        _engine.State = _engine.State! with
        {
            Seq = 7,
            Settings = Document(revision: 3, startWithWindows: true),
            Capture = CaptureState.Initial with { Replay = ReplayState.Buffering, MicMuted = false },
            TargetGame = new TargetGame(@"c:\games\game.exe", "Game.exe", 77),
            Warnings = [new Warning("w1", ErrorCodes.SettingsUnreadable, WarningSeverity.Error, "settings", "Defaults in use.", DateTimeOffset.UnixEpoch, false, null, true)],
        };

        _engine.RaiseChanged();
        Assert.False(model.General.StartWithWindows);
        RunPosted();

        Assert.Equal("Ready to save Clips in Game.exe", model.StatusLine.State);
        Assert.Equal("Defaults in use.", Assert.Single(model.StatusLine.Warnings).Message);
        Assert.True(model.General.StartWithWindows);
        Assert.Same(model.General, model.Settings.SelectedPage);
    }

    [Fact]
    public void TheStatusSaysWhyTheEngineIsNotRunning()
    {
        _engine.Status = EngineStatus.NotRunning;
        _engine.Problem = "Nothing answered.";
        _engine.State = null;

        var model = Create();

        Assert.Equal("Encore's background service isn't running. Nothing answered.", model.StatusLine.State);
        Assert.Empty(model.StatusLine.Warnings);
    }

    [Fact]
    public void TheStatusSaysTheEngineIsRestartingAndWhy()
    {
        _engine.Status = EngineStatus.Restarting;
        _engine.Problem = "Encore's background service exited with code 1.";

        var model = Create();

        Assert.Equal("Restarting Encore's background service… Encore's background service exited with code 1.", model.StatusLine.State);
    }

    [Fact]
    public void ChangingStartWithWindowsSendsAGlobalSetAgainstTheRenderedRevision()
    {
        _engine.State = _engine.State! with { Settings = Document(revision: 5, startWithWindows: false) };
        _engine.Reply = (_, _) => new SettingsUpdateResult(6);
        var model = Create();

        model.General.StartWithWindows = true;

        var update = Assert.IsType<SettingsUpdate>(Assert.Single(_engine.Sent));
        Assert.Equal(5, update.ExpectedRevision);
        var op = Assert.Single(update.Ops);
        Assert.Equal((SettingsScope.Global, "startWithWindows", SettingsOpKind.Set, true), (op.Scope, op.Key, op.Op, op.Value.GetBoolean()));
        Assert.True(model.General.StartWithWindows);
        Assert.Null(model.General.Message);
    }

    [Fact]
    public void ChoosingWhenTheOverlayDotShowsSendsAGlobalSet()
    {
        _engine.Reply = (_, _) => new SettingsUpdateResult(1);
        var model = Create();
        Assert.Equal(OverlayDotMode.Auto, model.General.OverlayDot);

        model.General.OverlayDot = OverlayDotMode.Always;

        var op = Assert.Single(Assert.IsType<SettingsUpdate>(Assert.Single(_engine.Sent)).Ops);
        Assert.Equal(("overlayDot", "always"), (op.Key, op.Value.GetString()));
    }

    [Fact]
    public void AConflictReloadsThePageFromTheCurrentDocument()
    {
        _engine.State = _engine.State! with { Settings = Document(revision: 1, startWithWindows: false) };
        var model = Create();
        _engine.Reply = (_, _) =>
        {
            // Another writer moved the document on; its settings.changed reached the state first.
            _engine.State = _engine.State with { Settings = Document(revision: 2, startWithWindows: false) };
            throw new ErrorCodeException(ErrorCodes.SettingsConflict, "Stale.");
        };

        model.General.StartWithWindows = true;

        Assert.False(model.General.StartWithWindows);
        Assert.Contains("reloaded", model.General.Message);
        _engine.Reply = (_, _) => new SettingsUpdateResult(3);
        model.General.StartWithWindows = true;
        Assert.Equal(2, ((SettingsUpdate)_engine.Sent[^1]).ExpectedRevision);
    }

    [Fact]
    public void ARefusalShowsItsCodeAndPutsTheDocumentsValueBack()
    {
        var model = Create();
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, "Disk full.");

        model.General.StartWithWindows = true;

        Assert.False(model.General.StartWithWindows);
        Assert.Equal("Not saved (settings-write-failed): Disk full.", model.General.Message);
    }

    private MainViewModel Create() => new(_engine, _posted.Add);

    private void RunPosted()
    {
        foreach (var action in _posted)
        {
            action();
        }

        _posted.Clear();
    }

    private static SettingsDocument Document(long revision, bool startWithWindows) => SettingsDocument.Empty with
    {
        Revision = revision,
        Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, JsonSerializer.SerializeToElement(startWithWindows, SettingsJsonContext.Default.Boolean)),
    };
}
