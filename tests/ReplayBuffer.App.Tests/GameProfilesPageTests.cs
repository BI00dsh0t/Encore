using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class GameProfilesPageTests
{
    private const string AlphaId = @"c:\a\a.exe";
    private const string BetaId = @"c:\b\b.exe";

    private static readonly AudioDevice Microphone = new("{in-1}", "Microphone", "Arctis", AudioFlow.Input, true);
    private static readonly LayoutDevice MicDevice = new(Guid.NewGuid(), "{in-1}", "Microphone", "Arctis", AudioFlow.Input, IsMic: false);
    private static readonly TrackLayout GlobalLayout = new(
        [MicDevice],
        [new Track(Keys.EverythingTrackId, "Everything", [MicDevice.Key], MuteAffected: true), new Track(Guid.NewGuid(), "Chat", [], MuteAffected: false)]);

    private static readonly VideoSettings GlobalVideo = Keys.Video.Default with { Width = 1280, Height = 720 };

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void ProfilesAreListedByNameAndEachEditSendsOneOpAtTheRenderedRevision()
    {
        SetProfiles();
        var updates = new List<SettingsUpdate>();
        _engine.Reply = (_, args) =>
        {
            updates.Add((SettingsUpdate)args);
            return new SettingsUpdateResult(4);
        };
        var page = Create().GameProfiles;

        Assert.Equal(
            [(AlphaId, "Alpha", true, false), (BetaId, "Beta", false, false)],
            page.Profiles.Select(p => (p.ProfileId, p.Name, p.Ignored, p.RecordingsEnabled)));

        page.Profiles[0].Name = "  ";
        Assert.Empty(updates);
        Assert.Equal("Alpha", page.Profiles[0].Name);

        page.Profiles[0].Name = " Apex ";
        page.Profiles[1].RecordingsEnabled = true;
        page.Profiles[1].Ignored = true;

        Assert.All(updates, u => Assert.Equal(3, u.ExpectedRevision));
        Assert.Equal(
            [
                (AlphaId, ProfileAttributes.Name, "\"Apex\""),
                (BetaId, ProfileAttributes.RecordingsEnabled, "true"),
                (BetaId, ProfileAttributes.Ignored, "true"),
            ],
            updates.Select(u => Assert.Single(u.Ops)).Select(o => (o.ProfileId, o.Key, o.Value.GetRawText())));
    }

    [Fact]
    public void AddSendsTheRawPathAndShowsARefusal()
    {
        _engine.Reply = (_, args) => new GameAdded(@"c:\games\game.exe");
        var page = Create().GameProfiles;

        page.AddPath = @"C:\Games\..\Games\Game.EXE";
        page.AddCommand.Execute(null);

        Assert.Equal(new GameAdd(@"C:\Games\..\Games\Game.EXE"), Assert.Single(_engine.Sent));
        Assert.Equal("", page.AddPath);

        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.InvalidArguments, "No such file.");
        page.AddPath = @"C:\Missing.exe";
        page.AddCommand.Execute(null);

        Assert.Equal("Not added (invalid-arguments): No such file.", page.Message);
        Assert.Equal(@"C:\Missing.exe", page.AddPath);
    }

    [Fact]
    public void ATrackLayoutOverrideStartsFromTheGlobalLayoutSavesAMicAndGoesBackToGlobal()
    {
        SetProfiles(revision: 3);
        var updates = Capture();
        var model = Create();
        var page = model.GameProfiles;

        page.SelectedProfile = page.Profiles[0];
        var layout = page.OverrideTrackLayout!;
        Assert.True(layout.UsesGlobal);
        Assert.False(layout.CanEdit);
        Assert.Equal(GlobalLayout.Tracks.Select(t => t.Id), layout.Tracks.Select(t => t.Id));

        layout.UsesGlobal = false;

        var start = Assert.Single(Assert.Single(updates).Ops);
        Assert.Equal((SettingsScope.Profile, AlphaId, "trackLayout", SettingsOpKind.Set), (start.Scope, start.ProfileId, start.Key, start.Op));
        Assert.True(JsonElement.DeepEquals(Keys.TrackLayout.ToJson(GlobalLayout), start.Value));

        SetProfiles(revision: 4, ("trackLayout", Keys.TrackLayout.ToJson(GlobalLayout)));
        _engine.RaiseChanged();
        Assert.True(layout.CanEdit);

        layout.Devices[0].IsMic = true;
        layout.SaveCommand.Execute(null);

        var save = Assert.Single(updates[1].Ops);
        Assert.Equal((SettingsScope.Profile, AlphaId, "trackLayout", SettingsOpKind.Set), (save.Scope, save.ProfileId, save.Key, save.Op));
        Assert.True(Keys.TrackLayout.TryRead(save.Value, out var saved));
        Assert.True(Assert.Single(saved.Devices).IsMic);
        Assert.Equal(GlobalLayout.Tracks.Select(t => t.Id), saved.Tracks.Select(t => t.Id));
        Assert.False(Assert.Single(model.TrackLayout.Devices).IsMic);

        SetProfiles(revision: 5, ("trackLayout", save.Value));
        _engine.RaiseChanged();
        layout.UsesGlobal = true;

        var remove = Assert.Single(updates[2].Ops);
        Assert.Equal((SettingsScope.Profile, AlphaId, "trackLayout", SettingsOpKind.Remove), (remove.Scope, remove.ProfileId, remove.Key, remove.Op));
        Assert.False(Assert.Single(layout.Devices).IsMic);
        Assert.False(layout.CanEdit);

        // The profile goes away, and so do the editors.
        _engine.State = _engine.State! with { Settings = _engine.State.Settings with { Revision = 6, Profiles = SettingsMaps.EmptyProfiles } };
        _engine.RaiseChanged();
        Assert.Null(page.SelectedProfile);
        Assert.Null(page.OverrideTrackLayout);
    }

    [Fact]
    public void OutputAndBufferWindowOverridesAreSentForTheProfileAndGoBackToGlobal()
    {
        SetProfiles(revision: 3);
        var updates = Capture();
        var page = Create().GameProfiles;
        page.SelectedProfile = page.Profiles[0];
        var output = page.OverrideOutput!;
        Assert.False(output.IsGlobal);
        Assert.False(output.CanEditVideo);

        output.VideoUsesGlobal = false;
        output.SelectedResolution = new ResolutionItem(1920, 1080, "1920 × 1080");
        output.BufferWindowUsesGlobal = false;
        output.BufferWindowText = "60";

        var ops = updates.Select(u => Assert.Single(u.Ops)).ToList();
        Assert.All(ops, o => Assert.Equal((SettingsScope.Profile, AlphaId, SettingsOpKind.Set), (o.Scope, o.ProfileId, o.Op)));
        Assert.Equal(["video", "video", "bufferWindow", "bufferWindow"], ops.Select(o => o.Key));
        Assert.True(Keys.Video.TryRead(ops[0].Value, out var copy));
        Assert.Equal(GlobalVideo, copy);
        Assert.True(Keys.Video.TryRead(ops[1].Value, out var video));
        Assert.Equal((1920, 1080), (video.Width, video.Height));
        Assert.Equal(["300", "60"], ops.Skip(2).Select(o => o.Value.GetRawText()));

        SetProfiles(revision: 4, ("video", ops[1].Value), ("bufferWindow", ops[3].Value));
        _engine.RaiseChanged();
        Assert.Equal("60", output.BufferWindowText);
        updates.Clear();

        output.VideoUsesGlobal = true;
        output.BufferWindowUsesGlobal = true;

        Assert.Equal(
            [(AlphaId, "video", SettingsOpKind.Remove), (AlphaId, "bufferWindow", SettingsOpKind.Remove)],
            updates.Select(u => Assert.Single(u.Ops)).Select(o => (o.ProfileId, o.Key, o.Op)));
        Assert.Equal(("300", 1280), (output.BufferWindowText, output.SelectedResolution!.Width));
        Assert.False(output.CanEditVideo || output.CanEditBufferWindow);
    }

    [Fact]
    public void TheViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        SetProfiles(revision: 3, ("trackLayout", Keys.TrackLayout.ToJson(GlobalLayout)));
        _engine.Reply = (_, _) => Empty.Value;
        var model = Create();
        model.SelectedPage = model.GameProfiles;
        model.GameProfiles.SelectedProfile = model.GameProfiles.Profiles[0];

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedPage.View;
            view.Measure(new Size(600, 1600));
            view.Arrange(new Rect(0, 0, 600, 1600));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 1600, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual();
            using (var context = white.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 1600));
            }

            bitmap.Render(white);
            bitmap.Render(view);

            // The HITL record's picture of the page.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "game-profile-overrides.png")))
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

        Assert.DoesNotContain(errors.Messages, m =>
            m.Contains("GameProfile", StringComparison.Ordinal)
            || m.Contains("TrackLayout", StringComparison.Ordinal)
            || m.Contains("ReplayBufferSettings", StringComparison.Ordinal));
    });

    private void SetProfiles() => SetProfiles(revision: 3);

    // Alpha and Beta, over a Global layout with a Mic device and 1280 x 720 video; Alpha has the given overrides.
    private void SetProfiles(long revision, params (string Key, JsonElement Value)[] alphaOverrides) => _engine.State = _engine.State! with
    {
        Devices = new AudioDeviceList([Microphone]),
        Settings = SettingsDocument.Empty with
        {
            Revision = revision,
            Global = SettingsMaps.EmptyValues
                .Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(GlobalLayout))
                .Add(Keys.Video.Name, Keys.Video.ToJson(GlobalVideo)),
            Profiles = SettingsMaps.EmptyProfiles
                .Add(BetaId, new GameProfile { Name = "Beta" })
                .Add(AlphaId, new GameProfile
                {
                    Name = "Alpha",
                    Ignored = true,
                    Overrides = SettingsMaps.EmptyValues.AddRange(alphaOverrides.Select(o => KeyValuePair.Create(o.Key, o.Value))),
                }),
        },
    };

    // Records and commits each settings.update; meters.* reply {}.
    private List<SettingsUpdate> Capture()
    {
        var updates = new List<SettingsUpdate>();
        _engine.Reply = (_, args) =>
        {
            if (args is SettingsUpdate update)
            {
                updates.Add(update);
                return new SettingsUpdateResult(update.ExpectedRevision + 1);
            }

            return Empty.Value;
        };
        return updates;
    }

    private MainViewModel Create() => new(_engine, action => action());
}
