using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Library;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class LibraryViewModelTests
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
        var viewModel = new MainViewModel(_engine, action => action()).Library;

        Assert.Equal(
            [
                ("new.mp4", "Beta", "", "Saving…"),
                ("mid.mp4", "Alpha", "1.5 MB", ""),
                ("old.mp4", "Other", "0.0 MB", ""),
            ],
            viewModel.List.Rows.Select(r => (r.Name, r.Game, r.Size, r.Lock)));
        Assert.True(viewModel.List.Rows[0].IsLocked);
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
        var viewModel = new MainViewModel(_engine, action => action()).Library;

        Assert.True(viewModel.Banner.IsCleanupBlocked);
        viewModel.Banner.AcknowledgeCommand.Execute(null);

        Assert.Equal(new WarningDismiss(CorruptWarning.Id), Assert.Single(_engine.Sent));
    }

    [Fact]
    public void AThumbnailIsAskedByAbsolutePathOnceTheItemIsWritten()
    {
        var asked = new List<string>();
        var viewModel = new LibraryViewModel(_engine, new LibraryThumbnails(path => { asked.Add(path); return null; }, work => work(), work => work()));
        const string Folder = @"C:\Videos\Encore";
        var writing = new LibraryItem(@"Game\clip.mp4", null, 0, DateTimeOffset.UnixEpoch, true);
        var state = _engine.State! with { LibraryFolder = Folder, Library = [writing] };

        viewModel.Render(state);
        Assert.Empty(asked);
        Assert.Equal("Saving…", viewModel.List.Rows[0].Lock);

        var written = state with { Library = [writing with { Size = 1536 * 1024, BeingWritten = false, LengthSeconds = 30 }] };
        viewModel.Render(written);
        viewModel.Render(written);

        Assert.Equal([System.IO.Path.Combine(Folder, writing.Path)], asked);
        Assert.False(viewModel.List.Rows[0].HasThumbnail);
        Assert.Equal(("0:30", "1.5 MB"), (viewModel.List.Rows[0].Length, viewModel.List.Rows[0].Size));
    }

    [Fact]
    public void AnHourLongTwoGigabyteItemReadsAsHoursAndGigabytes()
    {
        var viewModel = new LibraryViewModel(_engine);

        viewModel.Render(_engine.State! with { Library = [new LibraryItem("long.mp4", null, 2L << 30, DateTimeOffset.UnixEpoch, false, 3723)] });

        Assert.Equal(("1:02:03", "2.0 GB"), (viewModel.List.Rows[0].Length, viewModel.List.Rows[0].Size));
    }

    [Fact]
    public void TheStarSendsTheIdentityAndChangesOnlyWithTheSnapshot()
    {
        _engine.Reply = (_, _) => Empty.Value;
        var viewModel = new LibraryViewModel(_engine);
        var modified = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var item = new LibraryItem(@"Game\clip.mp4", null, 48, DateTimeOffset.UnixEpoch, false, Modified: modified);
        var state = _engine.State! with { Library = [item] };
        viewModel.Render(state);

        viewModel.List.Rows[0].FavouriteCommand.Execute(null);

        Assert.Equal(new LibrarySetFavourite(item.Path, 48, modified, true), Assert.Single(_engine.Sent));
        Assert.Equal((false, "☆"), (viewModel.List.Rows[0].IsFavourite, viewModel.List.Rows[0].Star));

        viewModel.Render(state with { Library = [item with { Favourite = true }] });
        Assert.Equal((true, "★"), (viewModel.List.Rows[0].IsFavourite, viewModel.List.Rows[0].Star));
    }

    [Fact]
    public void GroupedByGameTheHeadersAreTheGamesThenMonitorRecordingsThenOther()
    {
        var viewModel = new LibraryViewModel(_engine, time: Clock);

        viewModel.Render(ThreeGames());

        Assert.Equal(
            ["# Alpha", "a1.mp4", "s.mp4", "nofav.mp4", "old.mp4", "# Beta", "b.mp4", "# Gamma", "g.mp4",
             "# Monitor Recordings", "x.mp4", "# Other", "root.mp4"],
            Shown(viewModel));
        Assert.Equal(["All games", "Alpha", "Beta", "Gamma", "Monitor Recordings", "Other"], viewModel.List.Games.Select(g => g.Name));
    }

    [Fact]
    public void GroupedByDayTheHeadersAreTodayYesterdayThenEachDate()
    {
        var viewModel = new LibraryViewModel(_engine, time: Clock) { List = { GroupBy = LibraryGrouping.Day } };

        viewModel.Render(ThreeGames());

        Assert.Equal(
            ["# Today", "a1.mp4", "x.mp4", "# Yesterday", "s.mp4", "b.mp4", "root.mp4",
             "# 2026-11-01", "nofav.mp4", "# 2026-10-01", "old.mp4", "g.mp4"],
            Shown(viewModel));
    }

    [Fact]
    public void GameDateAndFavouriteFiltersCombine()
    {
        var viewModel = new LibraryViewModel(_engine, time: Clock);
        viewModel.Render(ThreeGames());

        viewModel.List.GameFilter = viewModel.List.Games.Single(g => g.Name == "Alpha").Key;
        viewModel.List.DateFilter = LibraryDateFilter.Last7Days;
        viewModel.List.FavouritesOnly = true;

        Assert.Equal(["# Alpha", "a1.mp4", "s.mp4"], Shown(viewModel));
    }

    [Fact]
    public void ANewItemLandsInItsGroupAndARenamedProfileRenamesItsGroup()
    {
        var viewModel = new LibraryViewModel(_engine, time: Clock);
        var state = ThreeGames();
        viewModel.Render(state);

        state = state with { Library = [.. state.Library, new LibraryItem(@"Beta\b2.mp4", BetaId, 1, Today.AddHours(-1), false)] };
        viewModel.Render(state);
        Assert.Equal(["# Beta", "b2.mp4", "b.mp4", "# Gamma"], Shown(viewModel).SkipWhile(e => e != "# Beta").Take(4));

        var alpha = viewModel.List.Games.Single(g => g.Name == "Alpha").Key;
        viewModel.List.GameFilter = alpha;
        viewModel.Render(state with
        {
            Settings = state.Settings with { Profiles = state.Settings.Profiles.SetItem(AlphaId, AlphaProfile with { Name = "Alpha 2" }) },
        });

        Assert.Equal(alpha, viewModel.List.GameFilter);
        Assert.Equal("# Alpha 2", Shown(viewModel)[0]);
    }

    [Fact]
    public void TheViewStateIsRestoredByANewPageAndAGarbageFileGivesTheDefaults()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"library-view-{Guid.NewGuid():N}.json");
        try
        {
            _ = new LibraryViewModel(_engine, viewStateFile: file)
            {
                List =
                {
                    GroupBy = LibraryGrouping.Day,
                    GameFilter = "profile:x",
                    DateFilter = LibraryDateFilter.Last30Days,
                    FavouritesOnly = true,
                },
            };

            // The first, empty render keeps a filter whose game isn't there yet.
            var restored = new LibraryViewModel(_engine, viewStateFile: file);
            restored.Render(_engine.State! with { Library = [] });
            Assert.Equal(
                (LibraryGrouping.Day, "profile:x", LibraryDateFilter.Last30Days, true),
                (restored.List.GroupBy, restored.List.GameFilter, restored.List.DateFilter, restored.List.FavouritesOnly));

            System.IO.File.WriteAllText(file, "{ not json");
            var garbage = new LibraryViewModel(_engine, viewStateFile: file);
            Assert.Equal(
                (LibraryGrouping.Game, (string?)null, LibraryDateFilter.All, false),
                (garbage.List.GroupBy, garbage.List.GameFilter, garbage.List.DateFilter, garbage.List.FavouritesOnly));
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
        var viewModel = new LibraryViewModel(_engine, time: Clock);
        string[] games = [AlphaId, BetaId, GammaId];
        var state = ThreeGames() with
        {
            Library = [.. Enumerable.Range(0, 5000).Select(i => new LibraryItem(
                $@"G{i % 3}\clip{i}.mp4", games[i % 3], i, Today.AddMinutes(-17 * i), false, Favourite: i % 2 == 0))],
        };
        viewModel.Render(state);

        var watch = Stopwatch.StartNew();
        viewModel.Render(state with { Library = [.. state.Library, new LibraryItem(@"G0\newest.mp4", AlphaId, 1, Today, false)] });
        viewModel.List.GameFilter = viewModel.List.Games[1].Key;
        viewModel.List.DateFilter = LibraryDateFilter.Last7Days;
        viewModel.List.GameFilter = null;
        viewModel.List.GroupBy = LibraryGrouping.Day;
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(5001, viewModel.List.Rows.Count);
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

    private static List<string> Shown(LibraryViewModel viewModel) =>
        [.. viewModel.List.Entries.Select(e => e is LibraryGroupHeader h ? "# " + h.Name : ((LibraryRow)e).Name)];

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
    public void TheTipsShowUntilClosedOnceAndTheEmptyLineOnlyOnAnEmptyUnfilteredLibrary()
    {
        var firstUseFile = Path.Combine(Path.GetTempPath(), "encore-first-use-" + Guid.NewGuid().ToString("N"), "first-use.json");
        try
        {
            var viewModel = new MainViewModel(_engine, action => action(), firstUseFile: firstUseFile).Library;
            Assert.True(viewModel.Tips.IsShown);
            Assert.True(viewModel.ShowsEmptyMessage);

            _engine.State = _engine.State! with
            {
                Settings = SettingsDocument.Empty with
                {
                    Revision = 2,
                    Global = SettingsMaps.EmptyValues
                        .Add(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(90))
                        .Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(
                            new HotkeySettings(new HotkeyBinding(0x77, HotkeyModifiers.Control), null, null, null, null, null))),
                },
            };
            _engine.RaiseChanged();
            Assert.Equal(("Ctrl\u2060+\u2060F8", "1 min 30 s"), (viewModel.Tips.SaveKey, viewModel.Tips.ClipLength));

            // Closed once, the tips stay closed, also for the next start.
            viewModel.Tips.CloseCommand.Execute(null);
            Assert.False(viewModel.Tips.IsShown);
            Assert.False(new MainViewModel(_engine, action => action(), firstUseFile: firstUseFile).Library.Tips.IsShown);

            viewModel.List.FavouritesOnly = true;
            Assert.False(viewModel.ShowsEmptyMessage);
            viewModel.List.FavouritesOnly = false;
            var at = DateTimeOffset.UnixEpoch;
            var match = new LibraryItem("Game\\match.mp4", null, 10, at, false, Modified: at, Kind: LibraryItemKinds.Recording);
            var clip = new LibraryItem("Game\\clip.mp4", null, 10, at, false, Modified: at, Kind: LibraryItemKinds.Clip);
            _engine.State = _engine.State with { Seq = 3, Library = [match, clip] };
            _engine.RaiseChanged();
            Assert.False(viewModel.ShowsEmptyMessage);
            Assert.Null(viewModel.List.NoResultsText);
            viewModel.List.FavouritesOnly = true;
            Assert.Equal("Nothing matches these filters.", viewModel.List.NoResultsText);
            viewModel.List.FavouritesOnly = false;

            // The kind badge: a Recording's only.
            var rows = viewModel.List.Entries.OfType<LibraryRow>().ToDictionary(r => r.Name, r => r.Badge);
            Assert.Equal("Recording", rows["match.mp4"]);
            Assert.Equal("", rows["clip.mp4"]);
        }
        finally
        {
            var folder = Path.GetDirectoryName(firstUseFile)!;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
