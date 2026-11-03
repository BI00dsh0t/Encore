using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Library;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class LibraryPageTests
{
    private const string AlphaId = @"c:\a\a.exe";

    private static readonly Warning CorruptWarning = new(
        "w-corrupt", ErrorCodes.LibraryCorrupt, WarningSeverity.Warning, "library", "rebuilt", DateTimeOffset.UnixEpoch, false, null, true);

    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void ItemsAreListedNewestFirstWithTheirGameSizeAndLock()
    {
        SetLibrary();
        var page = new MainViewModel(_engine, action => action()).Library;

        Assert.Equal(
            [
                ("new.mp4", "Beta", "", "Saving…"),
                ("mid.mp4", "Alpha", "1.5 MB", ""),
                ("old.mp4", "Other", "0.0 MB", ""),
            ],
            page.Rows.Select(r => (r.Name, r.Game, r.Size, r.Lock)));
        Assert.True(page.Rows[0].IsLocked);
    }

    [Fact]
    public void TheViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        SetLibrary();
        var model = new MainViewModel(_engine, action => action());

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.Library.View;
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

        Assert.DoesNotContain(errors.Messages, m => m.Contains("Library", StringComparison.Ordinal));
    });

    [Fact]
    public void ABlockedCleanupShowsTheBannerAndAcknowledgeDismissesTheWarning()
    {
        SetLibrary();
        _engine.Reply = (_, _) => Empty.Value;
        var page = new MainViewModel(_engine, action => action()).Library;

        Assert.True(page.IsCleanupBlocked);
        page.AcknowledgeCommand.Execute(null);

        Assert.Equal(new WarningDismiss(CorruptWarning.Id), Assert.Single(_engine.Sent));
    }

    [Fact]
    public void AThumbnailIsAskedByAbsolutePathOnceTheItemIsWritten()
    {
        var asked = new List<string>();
        var page = new LibraryPage(_engine, new LibraryThumbnails(path => { asked.Add(path); return null; }, work => work(), work => work()));
        const string Folder = @"C:\Videos\ReplayBuffer";
        var writing = new LibraryItem(@"Game\clip.mp4", null, 0, DateTimeOffset.UnixEpoch, true);
        var state = _engine.State! with { LibraryFolder = Folder, Library = [writing] };

        page.Render(state);
        Assert.Empty(asked);
        Assert.Equal("Saving…", page.Rows[0].Lock);

        var written = state with { Library = [writing with { Size = 1536 * 1024, BeingWritten = false, LengthSeconds = 30 }] };
        page.Render(written);
        page.Render(written);

        Assert.Equal([System.IO.Path.Combine(Folder, writing.Path)], asked);
        Assert.False(page.Rows[0].HasThumbnail);
        Assert.Equal(("0:30", "1.5 MB"), (page.Rows[0].Length, page.Rows[0].Size));
    }

    [Fact]
    public void AnHourLongTwoGigabyteItemReadsAsHoursAndGigabytes()
    {
        var page = new LibraryPage(_engine);

        page.Render(_engine.State! with { Library = [new LibraryItem("long.mp4", null, 2L << 30, DateTimeOffset.UnixEpoch, false, 3723)] });

        Assert.Equal(("1:02:03", "2.0 GB"), (page.Rows[0].Length, page.Rows[0].Size));
    }

    private void SetLibrary()
    {
        var day = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        _engine.State = _engine.State! with
        {
            Warnings = [CorruptWarning],
            CleanupBlocked = true,
            Settings = SettingsDocument.Empty with
            {
                Profiles = SettingsMaps.EmptyProfiles.Add(AlphaId, new GameProfile { Name = "Alpha" }),
            },
            Library =
            [
                new LibraryItem("old.mp4", null, 512, day.AddDays(-2), false),
                new LibraryItem(@"Beta\new.mp4", @"c:\b\b.exe", 0, day, true),
                new LibraryItem(@"Alpha\mid.mp4", AlphaId, 1536 * 1024, day.AddDays(-1), false),
            ],
        };
    }
}
