using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ImmutableValues = System.Collections.Immutable.ImmutableSortedDictionary<string, System.Text.Json.JsonElement>;

namespace ReplayBuffer.App.Tests;

/// <summary>Games (story 10.10; story 3.6's Game Profiles before it): the list, the drill-in, and a game's page.</summary>
[Collection(nameof(WpfCollection))]
public sealed class GameProfilesPageTests
{
    private const string AlphaId = @"c:\a\a.exe";
    private const string BetaId = @"c:\b\b.exe";
    private const string ChadId = @"c:\c\chad.exe";

    private static readonly AudioDevice Microphone = new("{in-1}", "Microphone", "Arctis", AudioFlow.Input, true);
    private static readonly LayoutDevice MicDevice = new(Guid.NewGuid(), "{in-1}", "Microphone", "Arctis", AudioFlow.Input, IsMic: false);
    private static readonly TrackLayout GlobalLayout = new(
        [MicDevice],
        [new Track(Keys.EverythingTrackId, "Everything", [MicDevice.Key], MuteAffected: true), new Track(Guid.NewGuid(), "Chat", [], MuteAffected: false)]);

    private static readonly VideoSettings GlobalVideo = Keys.Video.Default with { Width = 1280, Height = 720 };

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void TheListGroupsIgnoredLastAndShowsHowEachGameIsKnownWithNoProfileId()
    {
        SetProfilesWithAlpha(revision: 3, ("bufferWindow", Keys.BufferWindow.ToJson(60)), ("video", Keys.Video.ToJson(GlobalVideo)));
        _engine.State = _engine.State! with { TargetGame = new TargetGame(BetaId, "Beta", 7) };
        var page = Create().GameProfiles;

        Assert.Equal("Games", page.Title);
        Assert.Equal([("Beta", ""), ("Chad", ""), ("Alpha", "Ignored games")], page.Profiles.Select(p => (p.Name, p.Group)));
        Assert.Equal(["Detected automatically", "Added by hand", "Detected automatically · 2 settings changed"], page.Profiles.Select(p => p.Caption));
        Assert.Equal([true, false, false], page.Profiles.Select(p => p.IsCapturing));
        Assert.All(page.Profiles, p => Assert.DoesNotContain(@":\", p.Name + p.Caption, StringComparison.Ordinal));
    }

    [Fact]
    public void OpeningAGameShowsItsPageAndBackReturnsToItsRow()
    {
        SetProfiles(revision: 3);
        var page = Create().GameProfiles;
        var chad = page.Profiles[1];

        page.Open(chad);

        Assert.True(page.DrillIn.IsOpen);
        Assert.Equal(("Games", "Chad"), (page.DrillIn.ParentTitle, page.DrillIn.ItemTitle));
        Assert.Equal(ChadId, page.OpenGame!.ProgramPath);
        Assert.Equal("Added by hand", page.OpenGame.Source);

        page.DrillIn.BackCommand.Execute(null);

        Assert.Null(page.OpenGame);
        Assert.Equal(ChadId, page.LastOpenedId);
    }

    [Fact]
    public void ChangeForThisGameCopiesTheMainValueAndResetAsksThenRemovesIt()
    {
        SetProfiles(revision: 3);
        var updates = Capture();
        var page = Create().GameProfiles;
        page.Open(page.Profiles[0]);
        var game = page.OpenGame!;
        Assert.Equal(("Uses the main setting", "Save the last 300 seconds"), (game.Clips.Chip, game.Clips.Summary));
        Assert.Equal("1  Everything: Microphone" + Environment.NewLine + "2  Chat: no sounds", game.AudioTracks.Summary);

        game.AudioTracks.ChangeCommand.Execute(null);
        game.Clips.ChangeCommand.Execute(null);

        var ops = updates.Select(u => Assert.Single(u.Ops)).ToList();
        Assert.Equal([(BetaId, "trackLayout", SettingsOpKind.Set), (BetaId, "bufferWindow", SettingsOpKind.Set)], ops.Select(o => (o.ProfileId, o.Key, o.Op)));
        Assert.True(JsonElement.DeepEquals(Keys.TrackLayout.ToJson(GlobalLayout), ops[0].Value));
        Assert.Equal("300", ops[1].Value.GetRawText());

        // The engine commits both; the game now has its own.
        SetProfiles(revision: 5, beta: [("trackLayout", ops[0].Value), ("bufferWindow", ops[1].Value)]);
        _engine.RaiseChanged();
        Assert.True(game.Clips.IsChanged && game.AudioTracks.IsChanged);
        Assert.Equal(GlobalLayout.Tracks.Select(t => t.Id), game.Tracks.ToLayout().Tracks.Select(t => t.Id));
        updates.Clear();

        var asked = new List<string>();
        game.Confirm = (message, _) =>
        {
            asked.Add(message);
            return asked.Count > 1;
        };
        game.Clips.ResetCommand.Execute(null);
        Assert.Empty(updates);
        game.Clips.ResetCommand.Execute(null);

        Assert.Equal(["Reset Beta's Clip length to the main setting?", "Reset Beta's Clip length to the main setting?"], asked);
        var remove = Assert.Single(Assert.Single(updates).Ops);
        Assert.Equal((BetaId, "bufferWindow", SettingsOpKind.Remove), (remove.ProfileId, remove.Key, remove.Op));
    }

    [Fact]
    public void AMainChangeShowsAtOnceInAGameThatUsesIt()
    {
        SetProfiles(revision: 3);
        var page = Create().GameProfiles;
        page.Open(page.Profiles[0]);

        SetProfiles(revision: 4, global: [("bufferWindow", Keys.BufferWindow.ToJson(60))]);
        _engine.RaiseChanged();

        Assert.Equal("Save the last 60 seconds", page.OpenGame!.Clips.Summary);
        Assert.Equal("H.264 · 1280 × 720 · 60 fps · 40,000 kbps", page.OpenGame.Video.Summary);
    }

    [Fact]
    public void CaptureOffSetsIgnoredAndStopDetectingRemovesTheProgramFromManualGames()
    {
        SetProfiles(revision: 3);
        var updates = Capture();
        var page = Create().GameProfiles;
        var chad = page.Page(ChadId);

        chad.Capture = false;
        chad.StopDetectingCommand.Execute(null);

        var ops = updates.Select(u => Assert.Single(u.Ops)).ToList();
        Assert.Equal((SettingsScope.Profile, ChadId, ProfileAttributes.Ignored, "true"), (ops[0].Scope, ops[0].ProfileId, ops[0].Key, ops[0].Value.GetRawText()));
        Assert.Equal((SettingsScope.Global, Keys.ManualGames.Name, "[]"), (ops[1].Scope, ops[1].Key, ops[1].Value.GetRawText()));
    }

    [Fact]
    public void ADraftOnAGamesPageSurvivesGoingBackAndShowsOnItsRow()
    {
        SetProfiles(revision: 3, beta: [("trackLayout", Keys.TrackLayout.ToJson(GlobalLayout))]);
        _engine.Reply = (_, _) => Empty.Value;
        var model = Create();
        var page = model.GameProfiles;
        page.Open(page.Profiles[0]);
        var game = page.OpenGame!;
        game.Tracks.AddTrackCommand.Execute(null);
        Assert.True(game.HasUnsavedChanges);

        page.DrillIn.Close();
        page.Open(page.Profiles[0]);

        Assert.Same(game, page.OpenGame);
        Assert.Equal(3, game.Tracks.Tracks.Count);
        Assert.True(page.Profiles[0].HasUnsavedChanges);
        Assert.True(model.NavItems.Single(i => i.Page == page).HasUnsavedChanges);
    }

    [Fact]
    public void AGameRemovedFromTheDocumentReturnsItsPageToTheList()
    {
        SetProfiles(revision: 3);
        var page = Create().GameProfiles;
        page.Open(page.Profiles[0]);

        _engine.State = _engine.State! with { Settings = _engine.State.Settings with { Revision = 4, Profiles = _engine.State.Settings.Profiles.Remove(BetaId) } };
        _engine.RaiseChanged();

        Assert.False(page.DrillIn.IsOpen);
        Assert.Null(page.OpenGame);
        Assert.Equal("Beta is no longer in the list.", page.Message);
        Assert.Equal(["Chad", "Alpha"], page.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void AddAGameSendsTheChosenProgramAndShowsARefusal()
    {
        _engine.Reply = (_, args) => new GameAdded(@"c:\games\game.exe");
        var page = Create().GameProfiles;
        page.AddGame.Dialog = _ => @"C:\Games\Game.exe";

        page.AddGame.BrowseCommand.Execute(null);

        Assert.Equal(new GameAdd(@"C:\Games\Game.exe"), Assert.Single(_engine.Sent));
        Assert.Equal("Added. Game appears here the next time it runs.", page.Message);

        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.InvalidArguments, "No such file.");
        page.AddGame.BrowseCommand.Execute(null);
        Assert.Equal("Not added: No such file.", page.Message);
    }

    [Fact]
    public void TheListAndAGamesPageRenderWithoutBindingErrorsAndBackFocusesTheRow() => Sta.Run(() =>
    {
        SetProfilesWithAlpha(revision: 3, ("trackLayout", Keys.TrackLayout.ToJson(GlobalLayout)));
        _engine.Reply = (_, _) => Empty.Value;
        var model = Create();
        model.SelectedPage = model.GameProfiles;
        var page = model.GameProfiles;

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var window = new Window { Content = page.View, Width = 700, Height = 1400, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            Render(page.View, "games-list.png");

            page.Open(page.Profiles[2]);
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            Render(page.OpenGame!.View, "game-page.png");
            Assert.True(page.OpenGame.View.IsVisible);

            page.DrillIn.Close();
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            var list = (ListBox)page.View.FindName("GameList");
            Assert.Same(page.Profiles[2], list.SelectedItem);
            Assert.True(((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(page.Profiles[2])).IsKeyboardFocused);
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        Assert.DoesNotContain(errors.Messages, m =>
            m.Contains("Game", StringComparison.Ordinal)
            || m.Contains("TrackLayout", StringComparison.Ordinal)
            || m.Contains("ReplayBufferSettings", StringComparison.Ordinal)
            || m.Contains("Inherited", StringComparison.Ordinal));
    });

    private static void Render(FrameworkElement view, string name)
    {
        var size = new Size(Math.Max(1, view.ActualWidth), Math.Max(1, view.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var white = new DrawingVisual();
        using (var context = white.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(size));
        }

        bitmap.Render(white);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }

    // Beta and Chad (added by hand), then Alpha (Ignored), over a main layout with a Mic device and 1280 x 720 video.
    private void SetProfilesWithAlpha(long revision, params (string Key, JsonElement Value)[] alphaOverrides) =>
        SetProfiles(revision, alphaOverrides, [], []);

    private void SetProfiles(long revision, (string Key, JsonElement Value)[]? beta = null, (string Key, JsonElement Value)[]? global = null) =>
        SetProfiles(revision, [], beta ?? [], global ?? []);

    private void SetProfiles(long revision, (string Key, JsonElement Value)[] alpha, (string Key, JsonElement Value)[] beta, (string Key, JsonElement Value)[] global)
    {
        static ImmutableValues Values((string Key, JsonElement Value)[] values) =>
            SettingsMaps.EmptyValues.AddRange(values.Select(o => KeyValuePair.Create(o.Key, o.Value)));

        _engine.State = _engine.State! with
        {
            Devices = new AudioDeviceList([Microphone]),
            Settings = SettingsDocument.Empty with
            {
                Revision = revision,
                Global = Values(global)
                    .SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(GlobalLayout))
                    .SetItem(Keys.Video.Name, Keys.Video.ToJson(GlobalVideo))
                    .SetItem(Keys.ManualGames.Name, Keys.ManualGames.ToJson([ChadId])),
                Profiles = SettingsMaps.EmptyProfiles
                    .Add(BetaId, new GameProfile { Name = "Beta", Overrides = Values(beta) })
                    .Add(ChadId, new GameProfile { Name = "Chad" })
                    .Add(AlphaId, new GameProfile { Name = "Alpha", Ignored = true, Overrides = Values(alpha) }),
            },
        };
    }

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

    private MainViewModel Create() => new(_engine, action => action(), gameShell: new FakeGameShell());

    private sealed class FakeGameShell : IGameShell
    {
        public void ShowProgram(string path)
        {
        }

        public void OpenFolder(string folder)
        {
        }

        public void LoadIcon(string path, Action<ImageSource> loaded)
        {
        }
    }
}
