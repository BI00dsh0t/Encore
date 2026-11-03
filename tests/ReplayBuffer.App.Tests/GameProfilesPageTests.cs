using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class GameProfilesPageTests
{
    private const string AlphaId = @"c:\a\a.exe";
    private const string BetaId = @"c:\b\b.exe";

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
    public void TheViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        SetProfiles();
        var model = Create();
        model.SelectedPage = model.GameProfiles;

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

        Assert.DoesNotContain(errors.Messages, m => m.Contains("GameProfile", StringComparison.Ordinal));
    });

    private void SetProfiles() => _engine.State = _engine.State! with
    {
        Settings = SettingsDocument.Empty with
        {
            Revision = 3,
            Profiles = SettingsMaps.EmptyProfiles
                .Add(BetaId, new GameProfile { Name = "Beta" })
                .Add(AlphaId, new GameProfile { Name = "Alpha", Ignored = true }),
        },
    };

    private MainViewModel Create() => new(_engine, action => action());
}
