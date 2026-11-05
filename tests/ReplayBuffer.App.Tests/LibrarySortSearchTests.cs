using System.IO;
using ReplayBuffer.App.Library;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Tests;

/// <summary>Story 10.3: the Library's sort, Group by None and search, and the remembered view choices.</summary>
[Collection(nameof(WpfCollection))]
public sealed class LibrarySortSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

    private readonly FakeEngineSession _engine = new();

    // Newest to oldest: b (unknown length), "Clip 10", "clip 2", a.
    private static readonly LibraryItem[] Items =
    [
        new(@"B\b.mp4", null, 300, Now.AddHours(-1), false, null),
        new(@"A\Clip 10.mp4", null, 100, Now.AddHours(-2), false, 60),
        new(@"A\clip 2.mp4", null, 200, Now.AddHours(-3), false, 5),
        new(@"A\a.mp4", null, 100, Now.AddHours(-4), false, 30),
    ];

    [Theory]
    [InlineData(nameof(LibrarySort.NewestFirst), "b.mp4|Clip 10.mp4|clip 2.mp4|a.mp4")]
    [InlineData(nameof(LibrarySort.OldestFirst), "a.mp4|clip 2.mp4|Clip 10.mp4|b.mp4")]
    [InlineData(nameof(LibrarySort.Name), "a.mp4|b.mp4|clip 2.mp4|Clip 10.mp4")]
    [InlineData(nameof(LibrarySort.LargestFirst), "b.mp4|clip 2.mp4|Clip 10.mp4|a.mp4")]
    [InlineData(nameof(LibrarySort.LongestFirst), "Clip 10.mp4|a.mp4|clip 2.mp4|b.mp4")]
    [InlineData(nameof(LibrarySort.Game), "Clip 10.mp4|clip 2.mp4|a.mp4|b.mp4")]
    public void EachSortOrdersTheFlatListWithNoHeaders(string sort, string expected)
    {
        var page = new LibraryPage(_engine);
        page.Render(_engine.State! with { Library = Items });

        (page.GroupBy, page.Sort) = (LibraryGrouping.None, Enum.Parse<LibrarySort>(sort));

        Assert.All(page.Entries, e => Assert.IsType<LibraryRow>(e));
        Assert.Equal(expected, string.Join('|', page.Entries.Cast<LibraryRow>().Select(r => r.Name)));
    }

    [Fact]
    public void ASearchNarrowsTheRowsAndKeepsAHiddenSelectedItemSelected()
    {
        var page = new LibraryPage(_engine);
        page.Render(_engine.State! with { Library = Items });
        page.SelectAll();

        page.SearchText = "CLIP";

        Assert.Equal(["Clip 10.mp4", "clip 2.mp4"], page.Entries.OfType<LibraryRow>().Select(r => r.Name));
        Assert.Equal(("4 selected", "(2 hidden by filters)"), (page.SelectionText, page.HiddenText));
        Assert.Null(page.NoResultsText);

        page.SearchText = "zzz";
        Assert.Equal("No items match “zzz”.", page.NoResultsText);
        page.ClearSearchCommand.Execute(null);
        Assert.Equal(4, page.Entries.OfType<LibraryRow>().Count(r => r.IsSelected));
    }

    [Fact]
    public void SortAndGroupByAreRememberedAndAnOlderFileLoadsWithTheDefaults()
    {
        var folder = Directory.CreateTempSubdirectory("rb-library-view-").FullName;
        try
        {
            var file = Path.Combine(folder, "library-view.json");
            var page = new LibraryPage(_engine, viewStateFile: file);
            (page.GroupBy, page.Sort, page.SearchText) = (LibraryGrouping.None, LibrarySort.LargestFirst, "kept?");

            var reopened = new LibraryPage(_engine, viewStateFile: file);
            Assert.Equal((LibraryGrouping.None, LibrarySort.LargestFirst, ""), (reopened.GroupBy, reopened.Sort, reopened.SearchText));

            // A file written before story 10.3 has no sort.
            File.WriteAllText(file, """{ "GroupBy": "Day", "Game": null, "Date": "All", "FavouritesOnly": true }""");
            var older = LibraryViewState.Load(file);
            Assert.Equal((LibraryGrouping.Day, true, LibrarySort.NewestFirst), (older.GroupBy, older.FavouritesOnly, older.Sort));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
