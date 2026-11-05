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
    private const string BetaId = @"c:\b\b.exe";
    private const string GammaId = @"c:\g\g.exe";

    // 2026-11-03 12:00 UTC, with UTC as the local zone.
    private static readonly DateTimeOffset Today = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(Today);
    private static readonly GameProfile AlphaProfile = new() { Name = "Alpha", Folder = "Alpha" };

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

    [Fact]
    public void TheStarSendsTheIdentityAndChangesOnlyWithTheSnapshot()
    {
        _engine.Reply = (_, _) => Empty.Value;
        var page = new LibraryPage(_engine);
        var modified = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var item = new LibraryItem(@"Game\clip.mp4", null, 48, DateTimeOffset.UnixEpoch, false, Modified: modified);
        var state = _engine.State! with { Library = [item] };
        page.Render(state);

        page.Rows[0].FavouriteCommand.Execute(null);

        Assert.Equal(new LibrarySetFavourite(item.Path, 48, modified, true), Assert.Single(_engine.Sent));
        Assert.Equal((false, "☆"), (page.Rows[0].IsFavourite, page.Rows[0].Star));

        page.Render(state with { Library = [item with { Favourite = true }] });
        Assert.Equal((true, "★"), (page.Rows[0].IsFavourite, page.Rows[0].Star));
    }

    [Fact]
    public void GroupedByGameTheHeadersAreTheGamesThenMonitorRecordingsThenOther()
    {
        var page = new LibraryPage(_engine, time: Clock);

        page.Render(ThreeGames());

        Assert.Equal(
            ["# Alpha", "a1.mp4", "s.mp4", "nofav.mp4", "old.mp4", "# Beta", "b.mp4", "# Gamma", "g.mp4",
             "# Monitor Recordings", "x.mp4", "# Other", "root.mp4"],
            Shown(page));
        Assert.Equal(["All games", "Alpha", "Beta", "Gamma", "Monitor Recordings", "Other"], page.Games.Select(g => g.Name));
    }

    [Fact]
    public void GroupedByDayTheHeadersAreTodayYesterdayThenEachDate()
    {
        var page = new LibraryPage(_engine, time: Clock) { GroupBy = LibraryGrouping.Day };

        page.Render(ThreeGames());

        Assert.Equal(
            ["# Today", "a1.mp4", "x.mp4", "# Yesterday", "s.mp4", "b.mp4", "root.mp4",
             "# 2026-11-01", "nofav.mp4", "# 2026-10-01", "old.mp4", "g.mp4"],
            Shown(page));
    }

    [Fact]
    public void GameDateAndFavouriteFiltersCombine()
    {
        var page = new LibraryPage(_engine, time: Clock);
        page.Render(ThreeGames());

        page.GameFilter = page.Games.Single(g => g.Name == "Alpha").Key;
        page.DateFilter = LibraryDateFilter.Last7Days;
        page.FavouritesOnly = true;

        Assert.Equal(["# Alpha", "a1.mp4", "s.mp4"], Shown(page));
    }

    [Fact]
    public void ANewItemLandsInItsGroupAndARenamedProfileRenamesItsGroup()
    {
        var page = new LibraryPage(_engine, time: Clock);
        var state = ThreeGames();
        page.Render(state);

        state = state with { Library = [.. state.Library, new LibraryItem(@"Beta\b2.mp4", BetaId, 1, Today.AddHours(-1), false)] };
        page.Render(state);
        Assert.Equal(["# Beta", "b2.mp4", "b.mp4", "# Gamma"], Shown(page).SkipWhile(e => e != "# Beta").Take(4));

        var alpha = page.Games.Single(g => g.Name == "Alpha").Key;
        page.GameFilter = alpha;
        page.Render(state with
        {
            Settings = state.Settings with { Profiles = state.Settings.Profiles.SetItem(AlphaId, AlphaProfile with { Name = "Alpha 2" }) },
        });

        Assert.Equal(alpha, page.GameFilter);
        Assert.Equal("# Alpha 2", Shown(page)[0]);
    }

    [Fact]
    public void TheViewStateIsRestoredByANewPageAndAGarbageFileGivesTheDefaults()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"library-view-{Guid.NewGuid():N}.json");
        try
        {
            _ = new LibraryPage(_engine, viewStateFile: file)
            {
                GroupBy = LibraryGrouping.Day,
                GameFilter = "profile:x",
                DateFilter = LibraryDateFilter.Last30Days,
                FavouritesOnly = true,
            };

            // The first, empty render keeps a filter whose game isn't there yet.
            var restored = new LibraryPage(_engine, viewStateFile: file);
            restored.Render(_engine.State! with { Library = [] });
            Assert.Equal(
                (LibraryGrouping.Day, "profile:x", LibraryDateFilter.Last30Days, true),
                (restored.GroupBy, restored.GameFilter, restored.DateFilter, restored.FavouritesOnly));

            System.IO.File.WriteAllText(file, "{ not json");
            var garbage = new LibraryPage(_engine, viewStateFile: file);
            Assert.Equal(
                (LibraryGrouping.Game, (string?)null, LibraryDateFilter.All, false),
                (garbage.GroupBy, garbage.GameFilter, garbage.DateFilter, garbage.FavouritesOnly));
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void FiveThousandItemsGroupAndFilterInUnder200Milliseconds()
    {
        var page = new LibraryPage(_engine, time: Clock);
        string[] games = [AlphaId, BetaId, GammaId];
        var state = ThreeGames() with
        {
            Library = [.. Enumerable.Range(0, 5000).Select(i => new LibraryItem(
                $@"G{i % 3}\clip{i}.mp4", games[i % 3], i, Today.AddMinutes(-17 * i), false, Favourite: i % 2 == 0))],
        };
        page.Render(state);

        var watch = Stopwatch.StartNew();
        page.Render(state with { Library = [.. state.Library, new LibraryItem(@"G0\newest.mp4", AlphaId, 1, Today, false)] });
        page.GameFilter = page.Games[1].Key;
        page.DateFilter = LibraryDateFilter.Last7Days;
        page.GameFilter = null;
        page.GroupBy = LibraryGrouping.Day;
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(5001, page.Rows.Count);
    }

    private StateSnapshot ThreeGames()
    {
        var oct1 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        return _engine.State! with
        {
            Settings = SettingsDocument.Empty with
            {
                Profiles = SettingsMaps.EmptyProfiles
                    .Add(AlphaId, AlphaProfile)
                    .Add(BetaId, new GameProfile { Name = "Beta", Folder = "Beta" })
                    .Add(GammaId, new GameProfile { Name = "Gamma", Folder = "Gamma" }),
            },
            Library =
            [
                new LibraryItem(@"Alpha\a1.mp4", AlphaId, 1, Today.AddHours(-2), false, Favourite: true),
                new LibraryItem(@"Alpha\s.mp4", null, 1, Today.AddHours(-25), false, Favourite: true),
                new LibraryItem(@"Alpha\old.mp4", AlphaId, 1, oct1, false, Favourite: true),
                new LibraryItem(@"Alpha\nofav.mp4", AlphaId, 1, Today.AddDays(-2), false),
                new LibraryItem(@"Beta\b.mp4", BetaId, 1, Today.AddHours(-26), false, Favourite: true),
                new LibraryItem(@"Gamma\g.mp4", GammaId, 1, oct1.AddHours(-1), false),
                new LibraryItem(@"Monitor Recordings\x.mp4", null, 1, Today.AddHours(-3), false),
                new LibraryItem("root.mp4", null, 1, Today.AddHours(-27), false),
            ],
        };
    }

    private static List<string> Shown(LibraryPage page) =>
        [.. page.Entries.Select(e => e is LibraryGroupHeader h ? "# " + h.Name : ((LibraryRow)e).Name)];

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void TheWelcomeCardShowsOnlyOnAnEmptyUnfilteredLibraryAndFollowsTheSettings()
    {
        // Story 10.6.
        var page = new MainViewModel(_engine, action => action()).Library;
        Assert.True(page.ShowsWelcome);
        Assert.Equal("Audio: none yet — your Clips will be silent", page.Welcome.Audio);
        Assert.True(page.Welcome.IsSilent);

        var mic = new LayoutDevice(Guid.NewGuid(), "{in}", "Microphone", "Yeti", AudioFlow.Input, IsMic: true);
        _engine.State = _engine.State! with
        {
            LibraryFolder = @"D:\Clips",
            Settings = SettingsDocument.Empty with
            {
                Revision = 2,
                Global = SettingsMaps.EmptyValues
                    .Add(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(90))
                    .Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(0x77, HotkeyModifiers.Control), null, null, null, null, null)))
                    .Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(new TrackLayout([mic], [new Track(Keys.EverythingTrackId, "Everything", [mic.Key], true)]))),
            },
        };
        _engine.RaiseChanged();

        Assert.Equal(("Ctrl+F8", "to save the last 1 min 30 s as a Clip"), (page.Welcome.SaveKey, page.Welcome.LeadTail));
        Assert.Equal(("Audio: Microphone", "Change"), (page.Welcome.Audio, page.Welcome.AudioLink));
        Assert.Equal(@"Clips are saved in D:\Clips", page.Welcome.Folder);

        page.FavouritesOnly = true;
        Assert.False(page.ShowsWelcome);
        page.FavouritesOnly = false;
        _engine.State = _engine.State with { Seq = 3, Library = [new LibraryItem("Game\\match.mp4", null, 10, DateTimeOffset.UnixEpoch, false, Modified: DateTimeOffset.UnixEpoch, Kind: LibraryItemKinds.Recording), new LibraryItem("Game\\clip.mp4", null, 10, DateTimeOffset.UnixEpoch, false, Modified: DateTimeOffset.UnixEpoch, Kind: LibraryItemKinds.Clip)] };
        _engine.RaiseChanged();
        Assert.False(page.ShowsWelcome);

        // The kind badge: a Recording's only.
        var rows = page.Entries.OfType<LibraryRow>().ToDictionary(r => r.Name, r => r.Badge);
        Assert.Equal("Recording", rows["match.mp4"]);
        Assert.Equal("", rows["clip.mp4"]);
    }
}
