using System.Collections.Immutable;
using System.Text.Json;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

public sealed class MainViewModelTests
{
    private readonly FakeEngineSession _engine = new();
    private readonly List<Action> _posted = [];

    [Fact]
    public void ARefreshRendersTheSnapshotAndTheSettingsPages()
    {
        var model = Create();
        _engine.State = _engine.State! with
        {
            Seq = 7,
            Settings = Document(revision: 3, startWithWindows: true),
            Capture = CaptureState.Initial with { Replay = ReplayState.Buffering },
            Warnings = [new Warning("w1", ErrorCodes.SettingsUnreadable, WarningSeverity.Error, "settings", "Defaults in use.", DateTimeOffset.UnixEpoch, false, null, true)],
        };

        _engine.RaiseChanged();
        Assert.False(model.General.StartWithWindows);
        RunPosted();

        Assert.Equal("Connected to the engine.", model.Status);
        Assert.Equal("Process 4242, run run-1, event 7", model.EngineDetails);
        Assert.Contains("Replay Buffer buffering", model.Capture);
        Assert.Equal("3", model.SettingsRevision);
        Assert.Equal(["Error: Defaults in use. (settings-unreadable)"], model.Warnings);
        Assert.True(model.General.StartWithWindows);
        Assert.Same(model.General, model.SelectedPage);
    }

    [Fact]
    public void TheStatusSaysWhyTheEngineIsNotRunning()
    {
        _engine.Status = EngineStatus.NotRunning;
        _engine.Problem = "No engine answered.";
        _engine.State = null;

        var model = Create();

        Assert.Equal("The engine isn't running. No engine answered.", model.Status);
        Assert.Equal("", model.EngineDetails);
    }

    [Fact]
    public void TheStatusSaysTheEngineIsRestartingAndWhy()
    {
        _engine.Status = EngineStatus.Restarting;
        _engine.Problem = "The engine exited with code 1.";

        var model = Create();

        Assert.Equal("Restarting the engine… The engine exited with code 1.", model.Status);
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
