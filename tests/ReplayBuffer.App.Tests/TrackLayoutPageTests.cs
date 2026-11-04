using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class TrackLayoutPageTests
{
    private static readonly AudioDevice Speakers = new("{out-1}", "Speakers", "USB DAC", AudioFlow.Output, true);
    private static readonly AudioDevice Headset = new("{out-2}", "Headset", "Arctis", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{in-1}", "Microphone", "Arctis", AudioFlow.Input, true);

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void AFreshDocumentShowsTheDefaultLayoutAndIsNotDirty()
    {
        var page = Create().TrackLayout;

        Assert.Empty(page.Devices);
        var track = Assert.Single(page.Tracks);
        Assert.Equal((Keys.EverythingTrackId, "Everything"), (track.Id, track.Name));
        Assert.False(page.IsDirty);
        Assert.Equal("Track Layout", page.Title);
    }

    [Fact]
    public void AddingADeviceJoinsEverythingAndLeavesThePicker()
    {
        SetState(revision: 1, layout: null, Speakers, Headset);
        var page = Create().TrackLayout;
        Assert.Equal(2, page.Choices.Count);

        Add(page, Speakers);

        var device = Assert.Single(page.Devices);
        Assert.Equal(("{out-1}", "Speakers", "USB DAC", AudioFlow.Output, false), (device.Device.EndpointId, device.Device.FriendlyName, device.Device.ParentName, device.Device.Flow, device.Device.IsMic));
        Assert.NotEqual(Guid.Empty, device.Device.Key);
        Assert.True(device.Available);
        Assert.Equal([device.Device.Key], page.Tracks[0].DeviceKeys);
        Assert.Equal([Headset], page.Choices.Select(c => c.Device));
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void UncheckingADeviceOnEverythingKeepsItInTheLayout()
    {
        SetState(revision: 1, layout: null, Speakers);
        var page = Create().TrackLayout;
        Add(page, Speakers);

        page.Tracks[0].Checks.Single().IsChecked = false;

        Assert.Empty(page.ToLayout().Tracks[0].DeviceKeys);
        Assert.Single(page.ToLayout().Devices);
    }

    [Fact]
    public void RemovingADeviceTakesItOutOfEveryTrack()
    {
        SetState(revision: 1, layout: null, Speakers, Headset);
        var page = Create().TrackLayout;
        Add(page, Speakers);
        Add(page, Headset);
        page.AddTrackCommand.Execute(null);
        page.SelectedTrack!.Checks[0].IsChecked = true;

        page.SelectedDevice = page.Devices[0];
        page.RemoveDeviceCommand.Execute(null);

        var layout = page.ToLayout();
        var remaining = Assert.Single(layout.Devices);
        Assert.Equal("{out-2}", remaining.EndpointId);
        Assert.All(layout.Tracks, t => Assert.DoesNotContain(t.DeviceKeys, k => k != remaining.Key));
        Assert.Single(page.Tracks[1].Checks);
        Assert.Contains(page.Choices, c => c.Device == Speakers);
    }

    [Fact]
    public void TrackEditsKeepIdsAndOrder()
    {
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var a = page.SelectedTrack!;
        Assert.Equal("Track 2", a.Name);
        page.AddTrackCommand.Execute(null);
        var b = page.SelectedTrack!;
        b.Name = "Chat";

        page.MoveDownCommand.Execute(null);
        Assert.Equal(2, page.Tracks.IndexOf(b));
        page.MoveUpCommand.Execute(null);
        page.SelectedTrack = page.Tracks[0];
        page.MoveUpCommand.Execute(null);
        Assert.Equal([Keys.EverythingTrackId, b.Id, a.Id], page.ToLayout().Tracks.Select(t => t.Id));

        page.SelectedTrack = a;
        page.DeleteTrackCommand.Execute(null);

        var tracks = page.ToLayout().Tracks;
        Assert.Equal([(Keys.EverythingTrackId, "Everything", true), (b.Id, "Chat", false)], tracks.Select(t => (t.Id, t.Name, t.MuteAffected)));
    }

    [Fact]
    public void CheckingADeviceOnATrackAddsItsKey()
    {
        SetState(revision: 1, layout: null, Speakers);
        var page = Create().TrackLayout;
        Add(page, Speakers);
        page.AddTrackCommand.Execute(null);

        page.SelectedTrack!.Checks[0].IsChecked = true;

        Assert.Equal([page.Devices[0].Device.Key], page.ToLayout().Tracks[1].DeviceKeys);
        Assert.True(page.SelectedTrack.Checks[0].IsChecked);
    }

    [Fact]
    public void DevicesWithTheSameNameShowTheirParents()
    {
        var a = new AudioDevice("{a}", "Speakers", "Realtek", AudioFlow.Output, true);
        var b = new AudioDevice("{b}", "speakers", "USB DAC", AudioFlow.Output, true);
        SetState(revision: 1, layout: null, a, b, Mic);
        var page = Create().TrackLayout;

        Assert.Equal(["Speakers — Realtek (Output)", "speakers — USB DAC (Output)", "Microphone (Input)"], page.Choices.Select(c => c.Label));

        Add(page, a);
        Assert.Equal("Speakers — Realtek (Output)", page.Devices[0].Label);
    }

    [Fact]
    public void AnUnpluggedLayoutDeviceShowsAsUnavailableAndTheDraftIsKept()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var model = Create();
        var page = model.TrackLayout;
        Add(page, Speakers);
        Add(page, Headset);
        var draft = page.ToLayout();

        _engine.State = _engine.State! with { Seq = 2, Devices = new AudioDeviceList([Speakers, Mic]) };
        _engine.RaiseChanged();

        Assert.False(page.Devices[1].Available);
        Assert.Equal("unavailable", page.Devices[1].Note);
        Assert.True(page.Devices[0].Available);
        Assert.Equal(Json(draft), Json(page.ToLayout()));

        _engine.State = _engine.State with { Seq = 3, Devices = new AudioDeviceList([Speakers, Headset with { Available = false }, Mic]) };
        _engine.RaiseChanged();

        Assert.False(page.Devices[1].Available);
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void AnAbsentListedDeviceIsOfferedAsUnavailable()
    {
        SetState(revision: 1, layout: null, Speakers with { Available = false });
        var page = Create().TrackLayout;

        var choice = Assert.Single(page.Choices);
        Assert.Equal("unavailable", choice.Note);
    }

    [Fact]
    public void ThreeTracksSaveAsOneUpdateAgainstTheRenderedRevision()
    {
        SetState(revision: 4, layout: null, Speakers, Headset, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(5);
        var page = Create().TrackLayout;
        Add(page, Speakers);
        Add(page, Headset);
        Add(page, Mic);
        foreach (var (name, device) in new[] { ("Game", 0), ("Chat", 1), ("Mic", 2) })
        {
            page.AddTrackCommand.Execute(null);
            page.SelectedTrack!.Name = name;
            page.SelectedTrack.Checks[device].IsChecked = true;
        }

        page.SaveCommand.Execute(null);

        var update = Assert.IsType<SettingsUpdate>(Assert.Single(_engine.Sent));
        Assert.Equal(4, update.ExpectedRevision);
        var op = Assert.Single(update.Ops);
        Assert.Equal((SettingsScope.Global, "trackLayout", SettingsOpKind.Set), (op.Scope, op.Key, op.Op));
        Assert.True(Keys.TrackLayout.TryRead(op.Value, out var saved));
        var keys = saved.Devices.Select(d => d.Key).ToArray();
        Assert.Equal(["{out-1}", "{out-2}", "{in-1}"], saved.Devices.Select(d => d.EndpointId));
        Assert.Equal(["Everything", "Game", "Chat", "Mic"], saved.Tracks.Select(t => t.Name));
        Assert.Equal(keys, saved.Tracks[0].DeviceKeys);
        Assert.Equal([keys[0]], saved.Tracks[1].DeviceKeys);
        Assert.Equal([keys[1]], saved.Tracks[2].DeviceKeys);
        Assert.Equal([keys[2]], saved.Tracks[3].DeviceKeys);
        Assert.Equal(Keys.EverythingTrackId, saved.Tracks[0].Id);
        Assert.True(page.IsDirty);

        // The engine's settings.changed carries the saved value.
        _engine.State = _engine.State! with { Seq = 2, Settings = Document(5, saved) };
        _engine.RaiseChanged();

        Assert.False(page.IsDirty);
        Assert.Null(page.Message);
        Assert.Equal(4, page.Tracks.Count);
    }

    [Fact]
    public void ASaveWhileOneIsInFlightDoesNothing()
    {
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        _engine.Reply = (_, _) =>
        {
            // A second click before the first save's reply.
            page.SaveCommand.Execute(null);
            return new SettingsUpdateResult(1);
        };

        page.SaveCommand.Execute(null);

        Assert.Single(_engine.Sent);
        Assert.Null(page.Message);
    }

    [Fact]
    public void ABlankTrackNameRefusesTheSave()
    {
        var page = Create().TrackLayout;
        page.Tracks[0].Name = "  ";

        page.SaveCommand.Execute(null);

        Assert.Empty(_engine.Sent);
        Assert.Contains("needs a name", page.Message);
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void ARefusalKeepsTheDraft()
    {
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var draft = Json(page.ToLayout());
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, "Disk full.");

        page.SaveCommand.Execute(null);

        Assert.Equal(draft, Json(page.ToLayout()));
        Assert.True(page.IsDirty);
        Assert.Equal("Not saved (settings-write-failed): Disk full.", page.Message);
    }

    [Fact]
    public void AConflictWithTheLayoutUnchangedKeepsTheDraftAndSavesAgainstTheNewRevision()
    {
        SetState(revision: 1, layout: null);
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var draft = Json(page.ToLayout());
        _engine.Reply = (_, _) =>
        {
            _engine.State = _engine.State! with { Settings = Document(2, null) };
            throw new ErrorCodeException(ErrorCodes.SettingsConflict, "Stale.");
        };

        page.SaveCommand.Execute(null);

        Assert.Equal(draft, Json(page.ToLayout()));
        Assert.Contains("reloaded", page.Message);
        _engine.Reply = (_, _) => new SettingsUpdateResult(3);
        page.SaveCommand.Execute(null);
        Assert.Equal(2, ((SettingsUpdate)_engine.Sent[^1]).ExpectedRevision);
    }

    [Fact]
    public void ALayoutChangedElsewhereReloadsTheDraftWithAMessage()
    {
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var elsewhere = new TrackLayout([], [new Track(Guid.NewGuid(), "Other", [], false)]);

        _engine.State = _engine.State! with { Seq = 2, Settings = Document(2, elsewhere) };
        _engine.RaiseChanged();

        Assert.Equal(Json(elsewhere), Json(page.ToLayout()));
        Assert.False(page.IsDirty);
        Assert.Equal("The Track Layout changed elsewhere, so this page reloaded it.", page.Message);
    }

    [Fact]
    public void AnotherKeyChangingKeepsTheDraftAndTheNextSaveUsesTheNewRevision()
    {
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var draft = Json(page.ToLayout());

        _engine.State = _engine.State! with
        {
            Seq = 2,
            Settings = Document(7, null) with
            {
                Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, Keys.StartWithWindows.ToJson(true)),
            },
        };
        _engine.RaiseChanged();

        Assert.Equal(draft, Json(page.ToLayout()));
        Assert.True(page.IsDirty);
        _engine.Reply = (_, _) => new SettingsUpdateResult(8);
        page.SaveCommand.Execute(null);
        Assert.Equal(7, ((SettingsUpdate)Assert.Single(_engine.Sent)).ExpectedRevision);
    }

    [Fact]
    public void RevertReturnsToTheRenderedLayout()
    {
        var device = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: true);
        var rendered = new TrackLayout([device], [new Track(Keys.EverythingTrackId, "All", [device.Key], MuteAffected: true)]);
        SetState(revision: 3, layout: rendered, Speakers);
        var page = Create().TrackLayout;
        Assert.False(page.IsDirty);

        page.Tracks[0].Name = "Renamed";
        page.AddTrackCommand.Execute(null);
        Assert.True(page.IsDirty);
        page.RevertCommand.Execute(null);

        Assert.False(page.IsDirty);
        Assert.Equal(Json(rendered), Json(page.ToLayout()));
    }

    [Fact]
    public void MarkingAMicAndAMuteAffectedTrackSavesThemWithTheSameKeysAndIds()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(2);
        var page = Create().TrackLayout;
        Add(page, Speakers);
        Add(page, Headset);
        Add(page, Mic);
        var keys = page.Devices.Select(d => d.Device.Key).ToArray();
        Assert.Equal([false, false, true], page.Devices.Select(d => d.CanBeMic));

        page.Devices[2].IsMic = true;
        page.AddTrackCommand.Execute(null);
        var chat = page.SelectedTrack!;
        chat.Name = "Chat";
        chat.Checks[2].IsChecked = true;
        chat.MuteAffected = true;
        Assert.True(page.IsDirty);
        page.SaveCommand.Execute(null);

        var op = Assert.Single(Assert.IsType<SettingsUpdate>(Assert.Single(_engine.Sent)).Ops);
        Assert.True(Keys.TrackLayout.TryRead(op.Value, out var saved));
        Assert.Equal(keys, saved.Devices.Select(d => d.Key));
        Assert.Equal([false, false, true], saved.Devices.Select(d => d.IsMic));
        var everything = Keys.TrackLayout.Default.Tracks[0];
        Assert.Equal([(everything.Id, everything.MuteAffected), (chat.Id, true)], saved.Tracks.Select(t => (t.Id, t.MuteAffected)));
    }

    [Fact]
    public void OutputsFromTwoParentsOnATrackWarnButStillSave()
    {
        var speakers = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: false);
        var headset = new LayoutDevice(Guid.NewGuid(), "{out-2}", "Headset", "Arctis", AudioFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "{in-1}", "Microphone", "Arctis", AudioFlow.Input, IsMic: true);
        var rendered = new TrackLayout(
            [speakers, headset, mic],
            [
                new Track(Keys.EverythingTrackId, "Everything", [speakers.Key, headset.Key, mic.Key], MuteAffected: true),
                new Track(Guid.NewGuid(), "Chat", [mic.Key], MuteAffected: true),
                new Track(Guid.NewGuid(), "Game", [speakers.Key, mic.Key], MuteAffected: false),
            ]);
        SetState(revision: 1, layout: rendered, Speakers, Headset, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(2);
        var page = Create().TrackLayout;

        Assert.Equal("May count audio twice: mixes outputs from USB DAC, Arctis.", page.Tracks[0].Warning);
        Assert.True(page.Tracks[0].HasWarning);
        Assert.Equal(["", ""], page.Tracks.Skip(1).Select(t => t.Warning));

        page.Tracks[0].Name = "All";
        page.SaveCommand.Execute(null);
        Assert.Single(_engine.Sent);

        page.Tracks[0].Checks[1].IsChecked = false;
        Assert.Equal("", page.Tracks[0].Warning);
    }

    [Fact]
    public void AnOpenPageWatchesItsDraftAndShowsOnlyTheFedTracksLevel()
    {
        var model = CreateMetered(out var saved);
        Assert.Empty(_engine.Sent);

        Open(model);
        Assert.Equal(Json(saved), Json(Assert.IsType<MetersWatch>(Assert.Single(_engine.Sent)).Layout));

        var page = model.TrackLayout;
        page.AddTrackCommand.Execute(null);
        var draft = Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout;
        Assert.Equal(2, _engine.Sent.Count);
        Assert.Equal(3, draft.Tracks.Count);
        Assert.Equal(Json(page.ToLayout()), Json(draft));

        page.SelectedTrack!.Name = "Renamed";
        Assert.Equal(2, _engine.Sent.Count);

        _engine.State = _engine.State! with
        {
            Seq = 2,
            Meters = new MeterLevels([new TrackLevel(saved.Tracks[0].Id, 0), new TrackLevel(saved.Tracks[1].Id, 0.5f)]),
        };
        _engine.RaiseChanged();
        Assert.Equal(0, page.Tracks[0].Level);
        Assert.True(page.Tracks[1].Level > 0);
        Assert.Equal(0, page.Tracks[2].Level);
        Assert.True(page.IsDirty);

        _engine.State = _engine.State with { Seq = 1, EngineInstanceId = "run-2" };
        _engine.RaiseChanged();
        Assert.Equal(3, _engine.Sent.Count);
        Assert.Equal(Json(page.ToLayout()), Json(Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout));
    }

    [Fact]
    public void ClosingThePageUnwatchesAndZeroesTheLevels()
    {
        var model = CreateMetered(out var saved);
        Open(model);
        _engine.State = _engine.State! with { Seq = 2, Meters = new MeterLevels([new TrackLevel(saved.Tracks[1].Id, 0.5f)]) };
        _engine.RaiseChanged();
        Assert.True(model.TrackLayout.Tracks[1].Level > 0);

        model.SelectedPage = model.General;

        Assert.Same(Empty.Value, _engine.Sent[^1]);
        Assert.Single(_engine.Sent, a => a is Empty);
        Assert.All(model.TrackLayout.Tracks, t => Assert.Equal(0, t.Level));

        model.SelectedPage = model.TrackLayout;
        Assert.IsType<MetersWatch>(_engine.Sent[^1]);
        model.IsWindowShown = false;
        Assert.Same(Empty.Value, _engine.Sent[^1]);
        Assert.Equal(2, _engine.Sent.Count(a => a is Empty));

        // An unwatch lost to a dropped link goes out again once the same engine run is back.
        model.IsWindowShown = true;
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.EngineDisconnected, "Gone.");
        model.IsWindowShown = false;
        _engine.Reply = (_, _) => Empty.Value;
        _engine.RaiseChanged();
        Assert.Same(Empty.Value, _engine.Sent[^1]);
        Assert.Equal(4, _engine.Sent.Count(a => a is Empty));
    }

    [Fact]
    public void TheSettingsTabListsThePageAndItsViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        SetState(revision: 1, layout: null, Speakers, Headset with { Available = false }, Mic, new AudioDevice("{out-3}", "Line Out", "Realtek", AudioFlow.Output, true));
        var model = new MainViewModel(_engine, action => action());
        Assert.Equal(["General", "Replay Buffer", "Hotkeys", "Track Layout", "Game Profiles", "Feedback Sounds"], model.Pages.Select(p => p.Title));
        model.SelectedPage = model.TrackLayout;
        var page = model.TrackLayout;
        Add(page, Speakers);
        Add(page, Headset with { Available = false });
        Add(page, Mic);
        page.Devices[2].IsMic = true;
        page.AddTrackCommand.Execute(null);
        page.SelectedTrack!.MuteAffected = true;
        Assert.True(page.Tracks[0].HasWarning);
        page.SelectedChoice = page.Choices[0];

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedPage.View;
            view.Measure(new Size(600, 800));
            view.Arrange(new Rect(0, 0, 600, 800));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 800, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);

            Assert.True(view.ActualHeight > 0);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        Assert.DoesNotContain(errors.Messages, m => m.Contains("TrackLayout", StringComparison.Ordinal));
    });

    private static string Json(TrackLayout layout) => Keys.TrackLayout.ToJson(layout).GetRawText();

    private static SettingsDocument Document(long revision, TrackLayout? layout) => SettingsDocument.Empty with
    {
        Revision = revision,
        Global = layout is null
            ? SettingsMaps.EmptyValues
            : SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
    };

    private static void Add(TrackLayoutPage page, AudioDevice device)
    {
        page.SelectedChoice = page.Choices.Single(c => c.Device == device);
        page.AddDeviceCommand.Execute(null);
    }

    private void SetState(long revision, TrackLayout? layout, params AudioDevice[] devices) =>
        _engine.State = _engine.State! with { Settings = Document(revision, layout), Devices = new AudioDeviceList(devices) };

    private MainViewModel Create() => new(_engine, action => action());

    /// <summary>A view model over a saved layout of two Tracks, with meters.* replying <c>{}</c>.</summary>
    private MainViewModel CreateMetered(out TrackLayout saved)
    {
        saved = new TrackLayout([], [new Track(Keys.EverythingTrackId, "Everything", [], true), new Track(Guid.NewGuid(), "Chat", [], false)]);
        SetState(revision: 1, layout: saved);
        _engine.Reply = (_, _) => Empty.Value;
        return Create();
    }

    private static void Open(MainViewModel model)
    {
        model.IsWindowShown = true;
        model.IsSettingsTabSelected = true;
        model.SelectedPage = model.TrackLayout;
    }
}
