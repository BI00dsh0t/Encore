using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>Audio Tracks (story 10.5; the Track Layout page of story 2.5 before it): the cards, the picker, presets, save bar and meters.</summary>
[Collection(nameof(WpfCollection))]
public sealed class TrackLayoutPageTests
{
    private static readonly AudioDevice Speakers = new("{out-1}", "Speakers", "USB DAC", AudioFlow.Output, true, IsDefault: true);
    private static readonly AudioDevice Headset = new("{out-2}", "Headset", "Arctis", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{in-1}", "Microphone", "Arctis", AudioFlow.Input, true, IsDefault: true);

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void AFreshDocumentShowsTheDefaultLayoutAndIsNotDirty()
    {
        var page = Create().TrackLayout;

        var track = Assert.Single(page.Tracks);
        Assert.Equal((Keys.EverythingTrackId, "Everything", 1), (track.Id, track.Name, track.Number));
        Assert.Empty(track.Sources);
        Assert.Equal("No sounds — this track will be silent", track.Summary);
        Assert.False(page.IsDirty);
        Assert.Equal("Audio Tracks", page.Title);
    }

    [Fact]
    public void ASoundFromThePickerGoesOnThatTrackOnlyAndAnInputIsAMicrophone()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var chat = page.Tracks[1];

        page.OpenPicker(chat);
        Assert.Equal(["Sounds from your PC (playback)", "Sounds from your PC (playback)", "Microphones and other inputs"], page.PickerRows.Select(r => r.Section));
        Assert.Equal(["Windows default", "Arctis", "Windows default"], page.PickerRows.Select(r => r.Caption));
        page.Pick(page.PickerRows.Single(r => r.Device == Mic));

        Assert.False(page.IsPickerOpen);
        var layout = page.ToLayout();
        var mic = Assert.Single(layout.Devices);
        Assert.True(mic.IsMic);
        Assert.Empty(layout.Tracks[0].DeviceKeys);
        Assert.Equal([mic.Key], layout.Tracks[1].DeviceKeys);
        Assert.Equal(("Microphone", "microphone", "1 sound"), (chat.Sources[0].Label, chat.Sources[0].Kind, chat.Summary));
        Assert.True(chat.HasMicrophone);
        Assert.True(page.IsDirty);

        // Ctrl+click keeps the picker open; a sound already on the Track is listed but can't be added.
        page.OpenPicker(chat);
        Assert.False(page.PickerRows.Single(r => r.Device == Mic).CanAdd);
        page.Pick(page.PickerRows.Single(r => r.Device == Speakers), keepOpen: true);
        Assert.True(page.IsPickerOpen);
        Assert.True(page.PickerRows.Single(r => r.Device == Speakers).IsAdded);
        Assert.Equal(2, chat.Sources.Count);
    }

    [Fact]
    public void RemovingASoundFromItsLastTrackDropsItFromTheSavedLayout()
    {
        SetState(revision: 1, layout: null, Speakers);
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        page.AddSound(page.Tracks[0], Speakers);
        page.AddSound(page.Tracks[1], Speakers);
        var key = Assert.Single(page.ToLayout().Devices).Key;
        Assert.Equal([key], page.ToLayout().Tracks[1].DeviceKeys);

        page.RemoveSound(page.Tracks[0], page.Tracks[0].Sources[0]);
        Assert.Single(page.ToLayout().Devices);

        page.RemoveSound(page.Tracks[1], page.Tracks[1].Sources[0]);
        Assert.Empty(page.ToLayout().Devices);
    }

    [Fact]
    public void MoveUpAndDownReorderTheTracksKeepingTheirIds()
    {
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var a = page.Tracks[1];
        Assert.Equal("Track 2", a.Name);
        page.AddTrackCommand.Execute(null);
        var b = page.Tracks[2];
        b.Name = "Chat";

        b.MoveUpCommand.Execute(null);
        Assert.Equal([Keys.EverythingTrackId, b.Id, a.Id], page.ToLayout().Tracks.Select(t => t.Id));
        Assert.Equal([1, 2, 3], page.Tracks.Select(t => t.Number));
        page.Tracks[0].MoveUpCommand.Execute(null);
        a.MoveDownCommand.Execute(null);
        Assert.Equal([Keys.EverythingTrackId, b.Id, a.Id], page.ToLayout().Tracks.Select(t => t.Id));

        // An empty Track is removed without asking.
        page.Confirm = (_, _) => throw new InvalidOperationException("Asked.");
        a.RemoveCommand.Execute(null);
        Assert.Equal([(Keys.EverythingTrackId, "Everything", true), (b.Id, "Chat", false)], page.ToLayout().Tracks.Select(t => (t.Id, t.Name, t.MuteAffected)));
    }

    [Fact]
    public void RemovingATrackWithSoundsAsksFirst()
    {
        SetState(revision: 1, layout: null, Speakers);
        var page = Create().TrackLayout;
        page.AddSound(page.Tracks[0], Speakers);
        var asked = new List<string>();
        page.Confirm = (message, _) =>
        {
            asked.Add(message);
            return false;
        };

        page.Tracks[0].RemoveCommand.Execute(null);
        Assert.Single(page.Tracks);
        page.Confirm = (_, _) => true;
        page.Tracks[0].RemoveCommand.Execute(null);

        Assert.Equal(["Remove \"Everything\" and its 1 sound?"], asked);
        Assert.True(page.HasNoTracks);
    }

    [Fact]
    public void TreatAsAMicrophoneAndTheMuteSwitchSetIsMicAndMuteAffected()
    {
        SetState(revision: 1, layout: null, Speakers, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(2);
        var page = Create().TrackLayout;
        page.AddTrackCommand.Execute(null);
        var chat = page.Tracks[1];
        chat.Name = "Chat";
        page.AddSound(page.Tracks[0], Mic);
        page.AddSound(chat, Mic);
        page.AddSound(chat, Speakers);
        Assert.Equal([true, false], chat.Sources.Select(s => s.CanBeMic));

        // One record: unmarking it on one Track unmarks it on both, and hides the mute switch.
        chat.Sources[0].IsMic = false;
        Assert.False(page.Tracks[0].Sources[0].IsMic);
        Assert.Equal("input", page.Tracks[0].Sources[0].Kind);
        Assert.False(chat.HasMicrophone);
        chat.Sources[0].IsMic = true;
        Assert.True(chat.HasMicrophone);
        chat.MuteAffected = true;
        page.SaveCommand.Execute(null);

        var op = Assert.Single(Assert.IsType<SettingsUpdate>(Assert.Single(_engine.Sent)).Ops);
        Assert.True(Keys.TrackLayout.TryRead(op.Value, out var saved));
        Assert.Equal([("{in-1}", true), ("{out-1}", false)], saved.Devices.Select(d => (d.EndpointId, d.IsMic)));
        Assert.Equal([(Keys.EverythingTrackId, true), (chat.Id, true)], saved.Tracks.Select(t => (t.Id, t.MuteAffected)));
    }

    [Fact]
    public void APresetReplacesTheDraftOnlyAfterItsConfirmation()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var page = Create().TrackLayout;
        string? asked = null;
        page.Confirm = (message, _) =>
        {
            asked = message;
            return false;
        };

        page.ApplyPresetCommand.Execute(TrackLayoutPreset.ForEditing);
        Assert.Equal("Replace your audio tracks with For editing?", asked);
        Assert.Single(page.Tracks);
        Assert.False(page.IsDirty);

        page.Confirm = (_, _) => true;
        page.ApplyPresetCommand.Execute(TrackLayoutPreset.ForEditing);

        Assert.Equal(["Everything", "Game and chat", "Microphone"], page.Tracks.Select(t => t.Name));
        Assert.Equal(Keys.EverythingTrackId, page.Tracks[0].Id);
        Assert.Equal([["Speakers", "Microphone"], ["Speakers"], ["Microphone"]], page.Tracks.Select(t => t.Sources.Select(s => s.Label).ToArray()));
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void TheSaveBarAndNavDotShowWhileDirtyAndDiscardRestoresTheSavedLayout()
    {
        var device = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: false);
        var rendered = new TrackLayout([device], [new Track(Keys.EverythingTrackId, "All", [device.Key], MuteAffected: true)]);
        SetState(revision: 3, layout: rendered, Speakers);
        var model = Create();
        var page = model.TrackLayout;
        var nav = model.NavItems.Single(i => i.Page == page);
        Assert.False(page.IsDirty);
        Assert.False(nav.HasUnsavedChanges);

        page.Tracks[0].Name = "Renamed";
        page.AddTrackCommand.Execute(null);
        Assert.True(page.IsDirty);
        Assert.True(nav.HasUnsavedChanges);

        page.DiscardCommand.Execute(null);

        Assert.False(page.IsDirty);
        Assert.False(nav.HasUnsavedChanges);
        Assert.Equal(Json(rendered), Json(page.ToLayout()));
    }

    [Fact]
    public void AGameUsingTheMainTracksShowsAReadOnlySummary()
    {
        var speakers = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "{in-1}", "Microphone", "Arctis", AudioFlow.Input, IsMic: true);
        var main = new TrackLayout(
            [speakers, mic],
            [new Track(Keys.EverythingTrackId, "Everything", [speakers.Key, mic.Key], true), new Track(Guid.NewGuid(), "Chat", [], false)]);
        var profile = new TrackLayoutPage(new SettingsSender(_engine), _engine, @"c:\games\game.exe");

        profile.Render(Document(1, main));

        Assert.True(profile.ShowsSummary);
        Assert.False(profile.CanEdit);
        Assert.Equal(["1  Everything: Speakers, Microphone", "2  Chat: no sounds"], profile.Summary);
    }

    [Fact]
    public void DevicesWithTheSameNameShowTheirParents()
    {
        var a = new AudioDevice("{a}", "Speakers", "Realtek", AudioFlow.Output, true);
        var b = new AudioDevice("{b}", "speakers", "USB DAC", AudioFlow.Output, true);
        SetState(revision: 1, layout: null, a, b, Mic);
        var page = Create().TrackLayout;

        page.OpenPicker(page.Tracks[0]);
        Assert.Equal(["Speakers (Realtek)", "speakers (USB DAC)", "Microphone"], page.PickerRows.Select(r => r.Label));

        page.Pick(page.PickerRows[0]);
        Assert.Equal("Speakers (Realtek)", page.Tracks[0].Sources[0].Label);
    }

    [Fact]
    public void AnUnpluggedSourceShowsAsNotConnectedAndTheDraftIsKept()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var page = Create().TrackLayout;
        page.AddSound(page.Tracks[0], Speakers);
        page.AddSound(page.Tracks[0], Headset);
        var draft = page.ToLayout();

        _engine.State = _engine.State! with { Seq = 2, Devices = new AudioDeviceList([Speakers, Mic]) };
        _engine.RaiseChanged();

        var sources = page.Tracks[0].Sources;
        Assert.False(sources[1].Available);
        Assert.Equal("Not connected — records silence until it's back", sources[1].Note);
        Assert.True(sources[0].Available);
        Assert.Equal(Json(draft), Json(page.ToLayout()));
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void ThreeTracksSaveAsOneUpdateAgainstTheRenderedRevision()
    {
        SetState(revision: 4, layout: null, Speakers, Headset, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(5);
        var page = Create().TrackLayout;
        foreach (var device in new[] { Speakers, Headset, Mic })
        {
            page.AddSound(page.Tracks[0], device);
        }

        foreach (var (name, device) in new[] { ("Game", Speakers), ("Chat", Headset), ("Mic", Mic) })
        {
            page.AddTrackCommand.Execute(null);
            page.Tracks[^1].Name = name;
            page.AddSound(page.Tracks[^1], device);
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
        Assert.Equal([[keys[0]], [keys[1]], [keys[2]]], saved.Tracks.Skip(1).Select(t => t.DeviceKeys.ToArray()));
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
    public void ABlankTrackNameRefusesTheSaveOnTheBarAndOutlinesTheName()
    {
        var page = Create().TrackLayout;
        page.Tracks[0].Name = "  ";

        page.SaveCommand.Execute(null);

        Assert.Empty(_engine.Sent);
        Assert.Equal("Give every track a name to save.", page.SaveProblem);
        Assert.True(page.Tracks[0].HasNameProblem);
        Assert.True(page.IsDirty);

        page.Tracks[0].Name = "All";
        Assert.Null(page.SaveProblem);
        Assert.False(page.Tracks[0].HasNameProblem);
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
        Assert.Equal("Audio tracks were changed somewhere else, so this page shows the new ones.", page.Message);
    }

    [Fact]
    public void AReloadClosesThePickerSoNoPickLandsOnAStaleTrack()
    {
        // QA 10.5: a settings.changed on a clean page rebuilds the cards.
        SetState(revision: 1, layout: null, Speakers);
        var page = Create().TrackLayout;
        page.OpenPicker(page.Tracks[0]);

        _engine.State = _engine.State! with { Seq = 2, Settings = Document(2, null) with { Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, Keys.StartWithWindows.ToJson(true)) } };
        _engine.RaiseChanged();

        Assert.False(page.IsPickerOpen);
        page.OpenPicker(page.Tracks[0]);
        page.Pick(page.PickerRows.Single());
        Assert.Single(page.ToLayout().Tracks[0].DeviceKeys);
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

        Assert.Equal("You may hear some sounds twice: USB DAC and Arctis can both carry the same audio.", page.Tracks[0].Warning);
        Assert.True(page.Tracks[0].HasWarning);
        Assert.Equal(["", ""], page.Tracks.Skip(1).Select(t => t.Warning));

        page.Tracks[0].Name = "All";
        page.SaveCommand.Execute(null);
        Assert.Single(_engine.Sent);

        page.RemoveSound(page.Tracks[0], page.Tracks[0].Sources[1]);
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

        page.Tracks[^1].Name = "Renamed";
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
    public void EachSourceAndPickerRowShowsItsOwnLevelFromItsProbe()
    {
        var model = CreateMetered(out _);
        _engine.State = _engine.State! with { Devices = new AudioDeviceList([Speakers, Headset, Mic]) };
        _engine.RaiseChanged();
        Open(model);
        var page = model.TrackLayout;
        page.AddSound(page.Tracks[0], Speakers);

        // A probe per source; the picker adds one per listed device, and the saved layout has none.
        Assert.Single(Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout.Tracks, t => t.Name == MeterProbes.ProbeName);
        page.OpenPicker(page.Tracks[1]);
        var watched = Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout;
        Assert.Equal(3, watched.Tracks.Count(t => t.Name == MeterProbes.ProbeName));
        Assert.DoesNotContain(page.ToLayout().Tracks, t => t.Name == MeterProbes.ProbeName);

        _engine.State = _engine.State! with
        {
            Seq = 3,
            Meters = new MeterLevels([new TrackLevel(MeterProbes.TrackId("{out-1}", AudioFlow.Output), 0.5f), new TrackLevel(MeterProbes.TrackId("{in-1}", AudioFlow.Input), 1f)]),
        };
        _engine.RaiseChanged();

        Assert.True(page.Tracks[0].Sources[0].Level > 0);
        Assert.Equal(1, page.PickerRows.Single(r => r.Device == Mic).Level);
        Assert.Equal(0, page.PickerRows.Single(r => r.Device == Headset).Level);

        page.ClosePicker();
        Assert.Single(Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout.Tracks, t => t.Name == MeterProbes.ProbeName);
    }

    [Fact]
    public void AnOpenPageWatchesAgainAfterAReconnectToTheSameEngineRun()
    {
        // Story 10.4: the engine stops a watch when its last client leaves.
        var model = CreateMetered(out _);
        Open(model);
        Assert.Single(_engine.Sent);

        _engine.Status = EngineStatus.Disconnected;
        _engine.RaiseChanged();
        _engine.Status = EngineStatus.Connected;
        _engine.RaiseChanged();

        Assert.Equal(2, _engine.Sent.Count);
        Assert.IsType<MetersWatch>(_engine.Sent[^1]);
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
        Assert.Equal(["General", "Clips", "Hotkeys", "Audio Tracks", "Game Profiles", "Sounds", "Storage and Cleanup"], model.Pages.Select(p => p.Title));
        model.SelectedPage = model.TrackLayout;
        var page = model.TrackLayout;
        page.AddSound(page.Tracks[0], Speakers);
        page.AddSound(page.Tracks[0], Headset with { Available = false });
        page.AddSound(page.Tracks[0], Mic);
        page.AddTrackCommand.Execute(null);
        page.Tracks[1].MuteAffected = true;
        Assert.True(page.Tracks[0].HasWarning);

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

        Assert.DoesNotContain(errors.Messages, m => m.Contains("TrackLayout", StringComparison.Ordinal) || m.Contains("Audio", StringComparison.Ordinal));
    });

    private static string Json(TrackLayout layout) => Keys.TrackLayout.ToJson(layout).GetRawText();

    private static SettingsDocument Document(long revision, TrackLayout? layout) => SettingsDocument.Empty with
    {
        Revision = revision,
        Global = layout is null
            ? SettingsMaps.EmptyValues
            : SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
    };

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
