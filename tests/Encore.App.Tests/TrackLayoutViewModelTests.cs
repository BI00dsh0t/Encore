using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Engine;
using Encore.App.Settings;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>Audio Tracks: the cards, the picker, presets, save bar and meters.</summary>
[Collection(nameof(WpfCollection))]
public sealed class TrackLayoutViewModelTests
{
    private static readonly AudioDevice Speakers = new("{out-1}", "Speakers", "USB DAC", AudioFlow.Output, true, IsDefault: true);
    private static readonly AudioDevice Headset = new("{out-2}", "Headset", "Arctis", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{in-1}", "Microphone", "Arctis", AudioFlow.Input, true, IsDefault: true);

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void AFreshDocumentShowsTheDefaultLayoutAndIsNotDirty()
    {
        var viewModel = Create().TrackLayout;

        var track = Assert.Single(viewModel.Tracks);
        Assert.Equal((Keys.EverythingTrackId, "Everything", 1), (track.Id, track.Name, track.Number));
        Assert.Empty(track.Sources);
        Assert.Equal("No sounds — this track will be silent", track.Summary);
        Assert.False(viewModel.IsDirty);
        Assert.Equal("Audio Tracks", viewModel.Title);
    }

    [Fact]
    public void ASoundFromThePickerGoesOnThatTrackOnlyAndAnInputIsAMicrophone()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var chat = viewModel.Tracks[1];

        viewModel.Picker.Open(chat);
        Assert.Equal(["Sounds from your PC (playback)", "Sounds from your PC (playback)", "Microphones and other inputs"], viewModel.Picker.Rows.Select(r => r.Section));
        Assert.Equal(["Windows default", "Arctis", "Windows default"], viewModel.Picker.Rows.Select(r => r.Caption));
        viewModel.Picker.Pick(viewModel.Picker.Rows.Single(r => r.Device == Mic));

        Assert.False(viewModel.Picker.IsOpen);
        var layout = viewModel.ToLayout();
        var mic = Assert.Single(layout.Devices);
        Assert.True(mic.IsMic);
        Assert.Empty(layout.Tracks[0].DeviceKeys);
        Assert.Equal([mic.Key], layout.Tracks[1].DeviceKeys);
        Assert.Equal(("Microphone", "microphone", "1 sound"), (chat.Sources[0].Label, chat.Sources[0].Kind, chat.Summary));
        Assert.True(chat.HasMicrophone);
        Assert.True(viewModel.IsDirty);

        // Ctrl+click keeps the picker open; a sound already on the Track is listed but can't be added.
        viewModel.Picker.Open(chat);
        Assert.False(viewModel.Picker.Rows.Single(r => r.Device == Mic).CanAdd);
        viewModel.Picker.Pick(viewModel.Picker.Rows.Single(r => r.Device == Speakers), keepOpen: true);
        Assert.True(viewModel.Picker.IsOpen);
        Assert.True(viewModel.Picker.Rows.Single(r => r.Device == Speakers).IsAdded);
        Assert.Equal(2, chat.Sources.Count);
    }

    [Fact]
    public void RemovingASoundFromItsLastTrackDropsItFromTheSavedLayout()
    {
        SetState(revision: 1, layout: null, Speakers);
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        viewModel.AddSound(viewModel.Tracks[0], Speakers);
        viewModel.AddSound(viewModel.Tracks[1], Speakers);
        var key = Assert.Single(viewModel.ToLayout().Devices).Key;
        Assert.Equal([key], viewModel.ToLayout().Tracks[1].DeviceKeys);

        viewModel.RemoveSound(viewModel.Tracks[0], viewModel.Tracks[0].Sources[0]);
        Assert.Single(viewModel.ToLayout().Devices);

        viewModel.RemoveSound(viewModel.Tracks[1], viewModel.Tracks[1].Sources[0]);
        Assert.Empty(viewModel.ToLayout().Devices);
    }

    [Fact]
    public void MoveUpAndDownReorderTheTracksKeepingTheirIds()
    {
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var a = viewModel.Tracks[1];
        Assert.Equal("Track 2", a.Name);
        viewModel.AddTrackCommand.Execute(null);
        var b = viewModel.Tracks[2];
        b.Name = "Chat";

        b.MoveUpCommand.Execute(null);
        Assert.Equal([Keys.EverythingTrackId, b.Id, a.Id], viewModel.ToLayout().Tracks.Select(t => t.Id));
        Assert.Equal([1, 2, 3], viewModel.Tracks.Select(t => t.Number));
        viewModel.Tracks[0].MoveUpCommand.Execute(null);
        a.MoveDownCommand.Execute(null);
        Assert.Equal([Keys.EverythingTrackId, b.Id, a.Id], viewModel.ToLayout().Tracks.Select(t => t.Id));

        // An empty Track is removed without asking.
        viewModel.Confirm = (_, _) => throw new InvalidOperationException("Asked.");
        a.RemoveCommand.Execute(null);
        Assert.Equal([(Keys.EverythingTrackId, "Everything", true), (b.Id, "Chat", false)], viewModel.ToLayout().Tracks.Select(t => (t.Id, t.Name, t.MuteAffected)));
    }

    [Fact]
    public void RemovingATrackWithSoundsAsksFirst()
    {
        SetState(revision: 1, layout: null, Speakers);
        var viewModel = Create().TrackLayout;
        viewModel.AddSound(viewModel.Tracks[0], Speakers);
        var asked = new List<string>();
        viewModel.Confirm = (message, _) =>
        {
            asked.Add(message);
            return false;
        };

        viewModel.Tracks[0].RemoveCommand.Execute(null);
        Assert.Single(viewModel.Tracks);
        viewModel.Confirm = (_, _) => true;
        viewModel.Tracks[0].RemoveCommand.Execute(null);

        Assert.Equal(["Remove \"Everything\" and its 1 sound?"], asked);
        Assert.True(viewModel.HasNoTracks);
    }

    [Fact]
    public void TreatAsAMicrophoneAndTheMuteSwitchSetIsMicAndMuteAffected()
    {
        SetState(revision: 1, layout: null, Speakers, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(2);
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var chat = viewModel.Tracks[1];
        chat.Name = "Chat";
        viewModel.AddSound(viewModel.Tracks[0], Mic);
        viewModel.AddSound(chat, Mic);
        viewModel.AddSound(chat, Speakers);
        Assert.Equal([true, false], chat.Sources.Select(s => s.CanBeMic));

        // One record: unmarking it on one Track unmarks it on both, and hides the mute switch.
        chat.Sources[0].IsMic = false;
        Assert.False(viewModel.Tracks[0].Sources[0].IsMic);
        Assert.Equal("input", viewModel.Tracks[0].Sources[0].Kind);
        Assert.False(chat.HasMicrophone);
        chat.Sources[0].IsMic = true;
        Assert.True(chat.HasMicrophone);
        chat.MuteAffected = true;
        viewModel.SaveCommand.Execute(null);

        var op = Assert.Single(Assert.IsType<SettingsUpdate>(Assert.Single(_engine.Sent)).Ops);
        Assert.True(Keys.TrackLayout.TryRead(op.Value, out var saved));
        Assert.Equal([("{in-1}", true), ("{out-1}", false)], saved.Devices.Select(d => (d.EndpointId, d.IsMic)));
        Assert.Equal([(Keys.EverythingTrackId, true), (chat.Id, true)], saved.Tracks.Select(t => (t.Id, t.MuteAffected)));
    }

    [Fact]
    public void APresetReplacesTheDraftOnlyAfterItsConfirmation()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var viewModel = Create().TrackLayout;
        string? asked = null;
        viewModel.Confirm = (message, _) =>
        {
            asked = message;
            return false;
        };

        viewModel.ApplyPresetCommand.Execute(TrackLayoutPreset.ForEditing);
        Assert.Equal("Replace your audio tracks with For editing?", asked);
        Assert.Single(viewModel.Tracks);
        Assert.False(viewModel.IsDirty);

        viewModel.Confirm = (_, _) => true;
        viewModel.ApplyPresetCommand.Execute(TrackLayoutPreset.ForEditing);

        Assert.Equal(["Everything", "Game and chat", "Microphone"], viewModel.Tracks.Select(t => t.Name));
        Assert.Equal(Keys.EverythingTrackId, viewModel.Tracks[0].Id);
        Assert.Equal([["Speakers", "Microphone"], ["Speakers"], ["Microphone"]], viewModel.Tracks.Select(t => t.Sources.Select(s => s.Label).ToArray()));
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void TheSaveBarAndNavDotShowWhileDirtyAndDiscardRestoresTheSavedLayout()
    {
        var device = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: false);
        var rendered = new TrackLayout([device], [new Track(Keys.EverythingTrackId, "All", [device.Key], MuteAffected: true)]);
        SetState(revision: 3, layout: rendered, Speakers);
        var model = Create();
        var viewModel = model.TrackLayout;
        var nav = model.NavItems.Single(i => i.ViewModel == viewModel);
        Assert.False(viewModel.IsDirty);
        Assert.False(nav.HasUnsavedChanges);

        viewModel.Tracks[0].Name = "Renamed";
        viewModel.AddTrackCommand.Execute(null);
        Assert.True(viewModel.IsDirty);
        Assert.True(nav.HasUnsavedChanges);

        viewModel.DiscardCommand.Execute(null);

        Assert.False(viewModel.IsDirty);
        Assert.False(nav.HasUnsavedChanges);
        Assert.Equal(Json(rendered), Json(viewModel.ToLayout()));
    }

    [Fact]
    public void AGameUsingTheMainTracksShowsAReadOnlySummary()
    {
        var speakers = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "{in-1}", "Microphone", "Arctis", AudioFlow.Input, IsMic: true);
        var main = new TrackLayout(
            [speakers, mic],
            [new Track(Keys.EverythingTrackId, "Everything", [speakers.Key, mic.Key], true), new Track(Guid.NewGuid(), "Chat", [], false)]);
        var profile = new TrackLayoutViewModel(new SettingsSender(_engine), _engine, @"c:\games\game.exe");

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
        var viewModel = Create().TrackLayout;

        viewModel.Picker.Open(viewModel.Tracks[0]);
        Assert.Equal(["Speakers (Realtek)", "speakers (USB DAC)", "Microphone"], viewModel.Picker.Rows.Select(r => r.Label));

        viewModel.Picker.Pick(viewModel.Picker.Rows[0]);
        Assert.Equal("Speakers (Realtek)", viewModel.Tracks[0].Sources[0].Label);
    }

    [Fact]
    public void AnUnpluggedSourceShowsAsNotConnectedAndTheDraftIsKept()
    {
        SetState(revision: 1, layout: null, Speakers, Headset, Mic);
        var viewModel = Create().TrackLayout;
        viewModel.AddSound(viewModel.Tracks[0], Speakers);
        viewModel.AddSound(viewModel.Tracks[0], Headset);
        var draft = viewModel.ToLayout();

        _engine.State = _engine.State! with { Seq = 2, Devices = new AudioDeviceList([Speakers, Mic]) };
        _engine.RaiseChanged();

        var sources = viewModel.Tracks[0].Sources;
        Assert.False(sources[1].Available);
        Assert.Equal("Not connected — records silence until it's back", sources[1].Note);
        Assert.True(sources[0].Available);
        Assert.Equal(Json(draft), Json(viewModel.ToLayout()));
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void ThreeTracksSaveAsOneUpdateAgainstTheRenderedRevision()
    {
        SetState(revision: 4, layout: null, Speakers, Headset, Mic);
        _engine.Reply = (_, _) => new SettingsUpdateResult(5);
        var viewModel = Create().TrackLayout;
        foreach (var device in new[] { Speakers, Headset, Mic })
        {
            viewModel.AddSound(viewModel.Tracks[0], device);
        }

        foreach (var (name, device) in new[] { ("Game", Speakers), ("Chat", Headset), ("Mic", Mic) })
        {
            viewModel.AddTrackCommand.Execute(null);
            viewModel.Tracks[^1].Name = name;
            viewModel.AddSound(viewModel.Tracks[^1], device);
        }

        viewModel.SaveCommand.Execute(null);

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
        Assert.True(viewModel.IsDirty);

        // The engine's settings.changed carries the saved value.
        _engine.State = _engine.State! with { Seq = 2, Settings = Document(5, saved) };
        _engine.RaiseChanged();

        Assert.False(viewModel.IsDirty);
        Assert.Null(viewModel.Message);
        Assert.Equal(4, viewModel.Tracks.Count);
    }

    [Fact]
    public void ASaveWhileOneIsInFlightDoesNothing()
    {
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        _engine.Reply = (_, _) =>
        {
            // A second click before the first save's reply.
            viewModel.SaveCommand.Execute(null);
            return new SettingsUpdateResult(1);
        };

        viewModel.SaveCommand.Execute(null);

        Assert.Single(_engine.Sent);
        Assert.Null(viewModel.Message);
    }

    [Fact]
    public void ABlankTrackNameRefusesTheSaveOnTheBarAndOutlinesTheName()
    {
        var viewModel = Create().TrackLayout;
        viewModel.Tracks[0].Name = "  ";

        viewModel.SaveCommand.Execute(null);

        Assert.Empty(_engine.Sent);
        Assert.Equal("Give every track a name to save.", viewModel.SaveProblem);
        Assert.True(viewModel.Tracks[0].HasNameProblem);
        Assert.True(viewModel.IsDirty);

        viewModel.Tracks[0].Name = "All";
        Assert.Null(viewModel.SaveProblem);
        Assert.False(viewModel.Tracks[0].HasNameProblem);
    }

    [Fact]
    public void ARefusalKeepsTheDraft()
    {
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var draft = Json(viewModel.ToLayout());
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, "Disk full.");

        viewModel.SaveCommand.Execute(null);

        Assert.Equal(draft, Json(viewModel.ToLayout()));
        Assert.True(viewModel.IsDirty);
        Assert.Equal("Not saved (settings-write-failed): Disk full.", viewModel.Message);
    }

    [Fact]
    public void AConflictWithTheLayoutUnchangedKeepsTheDraftAndSavesAgainstTheNewRevision()
    {
        SetState(revision: 1, layout: null);
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var draft = Json(viewModel.ToLayout());
        _engine.Reply = (_, _) =>
        {
            _engine.State = _engine.State! with { Settings = Document(2, null) };
            throw new ErrorCodeException(ErrorCodes.SettingsConflict, "Stale.");
        };

        viewModel.SaveCommand.Execute(null);

        Assert.Equal(draft, Json(viewModel.ToLayout()));
        Assert.Contains("reloaded", viewModel.Message);
        _engine.Reply = (_, _) => new SettingsUpdateResult(3);
        viewModel.SaveCommand.Execute(null);
        Assert.Equal(2, ((SettingsUpdate)_engine.Sent[^1]).ExpectedRevision);
    }

    [Fact]
    public void ALayoutChangedElsewhereReloadsTheDraftWithAMessage()
    {
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var elsewhere = new TrackLayout([], [new Track(Guid.NewGuid(), "Other", [], false)]);

        _engine.State = _engine.State! with { Seq = 2, Settings = Document(2, elsewhere) };
        _engine.RaiseChanged();

        Assert.Equal(Json(elsewhere), Json(viewModel.ToLayout()));
        Assert.False(viewModel.IsDirty);
        Assert.Equal("Audio tracks were changed somewhere else, so this page shows the new ones.", viewModel.Message);
    }

    [Fact]
    public void AReloadClosesThePickerSoNoPickLandsOnAStaleTrack()
    {
        // A settings.changed on a clean page rebuilds the cards.
        SetState(revision: 1, layout: null, Speakers);
        var viewModel = Create().TrackLayout;
        viewModel.Picker.Open(viewModel.Tracks[0]);

        _engine.State = _engine.State! with { Seq = 2, Settings = Document(2, null) with { Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, Keys.StartWithWindows.ToJson(true)) } };
        _engine.RaiseChanged();

        Assert.False(viewModel.Picker.IsOpen);
        viewModel.Picker.Open(viewModel.Tracks[0]);
        viewModel.Picker.Pick(viewModel.Picker.Rows.Single());
        Assert.Single(viewModel.ToLayout().Tracks[0].DeviceKeys);
    }

    [Fact]
    public void AnotherKeyChangingKeepsTheDraftAndTheNextSaveUsesTheNewRevision()
    {
        var viewModel = Create().TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var draft = Json(viewModel.ToLayout());

        _engine.State = _engine.State! with
        {
            Seq = 2,
            Settings = Document(7, null) with
            {
                Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, Keys.StartWithWindows.ToJson(true)),
            },
        };
        _engine.RaiseChanged();

        Assert.Equal(draft, Json(viewModel.ToLayout()));
        Assert.True(viewModel.IsDirty);
        _engine.Reply = (_, _) => new SettingsUpdateResult(8);
        viewModel.SaveCommand.Execute(null);
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
        var viewModel = Create().TrackLayout;

        Assert.Equal("You may hear some sounds twice: USB DAC and Arctis can both carry the same audio.", viewModel.Tracks[0].Warning);
        Assert.True(viewModel.Tracks[0].HasWarning);
        Assert.Equal(["", ""], viewModel.Tracks.Skip(1).Select(t => t.Warning));

        viewModel.Tracks[0].Name = "All";
        viewModel.SaveCommand.Execute(null);
        Assert.Single(_engine.Sent);

        viewModel.RemoveSound(viewModel.Tracks[0], viewModel.Tracks[0].Sources[1]);
        Assert.Equal("", viewModel.Tracks[0].Warning);
    }

    [Fact]
    public void AnOpenPageWatchesItsDraftAndShowsOnlyTheFedTracksLevel()
    {
        var model = CreateMetered(out var saved);
        Assert.Empty(_engine.Sent);

        Open(model);
        Assert.Equal(Json(saved), Json(Assert.IsType<MetersWatch>(Assert.Single(_engine.Sent)).Layout));

        var viewModel = model.TrackLayout;
        viewModel.AddTrackCommand.Execute(null);
        var draft = Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout;
        Assert.Equal(2, _engine.Sent.Count);
        Assert.Equal(3, draft.Tracks.Count);
        Assert.Equal(Json(viewModel.ToLayout()), Json(draft));

        viewModel.Tracks[^1].Name = "Renamed";
        Assert.Equal(2, _engine.Sent.Count);

        _engine.State = _engine.State! with
        {
            Seq = 2,
            Meters = new MeterLevels([new TrackLevel(saved.Tracks[0].Id, 0), new TrackLevel(saved.Tracks[1].Id, 0.5f)]),
        };
        _engine.RaiseChanged();
        Assert.Equal(0, viewModel.Tracks[0].Level);
        Assert.True(viewModel.Tracks[1].Level > 0);
        Assert.Equal(0, viewModel.Tracks[2].Level);
        Assert.True(viewModel.IsDirty);

        _engine.State = _engine.State with { Seq = 1, EngineInstanceId = "run-2" };
        _engine.RaiseChanged();
        Assert.Equal(3, _engine.Sent.Count);
        Assert.Equal(Json(viewModel.ToLayout()), Json(Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout));
    }

    [Fact]
    public void EachSourceAndPickerRowShowsItsOwnLevelFromItsProbe()
    {
        var model = CreateMetered(out _);
        _engine.State = _engine.State! with { Devices = new AudioDeviceList([Speakers, Headset, Mic]) };
        _engine.RaiseChanged();
        Open(model);
        var viewModel = model.TrackLayout;
        viewModel.AddSound(viewModel.Tracks[0], Speakers);

        // A probe per source; the picker adds one per listed device, and the saved layout has none.
        Assert.Single(Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout.Tracks, t => t.Name == MeterProbes.ProbeName);
        viewModel.Picker.Open(viewModel.Tracks[1]);
        var watched = Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout;
        Assert.Equal(3, watched.Tracks.Count(t => t.Name == MeterProbes.ProbeName));
        Assert.DoesNotContain(viewModel.ToLayout().Tracks, t => t.Name == MeterProbes.ProbeName);

        _engine.State = _engine.State! with
        {
            Seq = 3,
            Meters = new MeterLevels([new TrackLevel(MeterProbes.TrackId("{out-1}", AudioFlow.Output), 0.5f), new TrackLevel(MeterProbes.TrackId("{in-1}", AudioFlow.Input), 1f)]),
        };
        _engine.RaiseChanged();

        Assert.True(viewModel.Tracks[0].Sources[0].Level > 0);
        Assert.Equal(1, viewModel.Picker.Rows.Single(r => r.Device == Mic).Level);
        Assert.Equal(0, viewModel.Picker.Rows.Single(r => r.Device == Headset).Level);

        viewModel.Picker.Close();
        Assert.Single(Assert.IsType<MetersWatch>(_engine.Sent[^1]).Layout.Tracks, t => t.Name == MeterProbes.ProbeName);
    }

    [Fact]
    public void AnOpenPageWatchesAgainAfterAReconnectToTheSameEngineRun()
    {
        // The engine stops a watch when its last client leaves.
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

        model.SelectedSettingsViewModel = model.General;

        Assert.Same(Empty.Value, _engine.Sent[^1]);
        Assert.Single(_engine.Sent, a => a is Empty);
        Assert.All(model.TrackLayout.Tracks, t => Assert.Equal(0, t.Level));

        model.SelectedSettingsViewModel = model.TrackLayout;
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
        Assert.Equal(["General", "Clips", "Hotkeys", "Audio Tracks", "Games", "Sounds", "Storage and Cleanup"], model.SettingsViewModels.Select(p => p.Title));
        model.SelectedSettingsViewModel = model.TrackLayout;
        var viewModel = model.TrackLayout;
        viewModel.AddSound(viewModel.Tracks[0], Speakers);
        viewModel.AddSound(viewModel.Tracks[0], Headset with { Available = false });
        viewModel.AddSound(viewModel.Tracks[0], Mic);
        viewModel.AddTrackCommand.Execute(null);
        viewModel.Tracks[1].MuteAffected = true;
        Assert.True(viewModel.Tracks[0].HasWarning);

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedSettingsViewModel.View;
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
        model.SelectedSettingsViewModel = model.TrackLayout;
    }
}
