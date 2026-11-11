using System.IO;
using Encore.App.Library;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

/// <summary>The Library's sort, Group by None and search, and the remembered view choices.</summary>
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
        var viewModel = new LibraryViewModel(_engine);
        viewModel.Render(_engine.State! with { Library = Items });

        (viewModel.List.GroupBy, viewModel.List.Sort) = (LibraryGrouping.None, Enum.Parse<LibrarySort>(sort));

        Assert.All(viewModel.List.Entries, e => Assert.IsType<LibraryRow>(e));
        Assert.Equal(expected, string.Join('|', viewModel.List.Entries.Cast<LibraryRow>().Select(r => r.Name)));
    }

    [Fact]
    public void ASearchNarrowsTheRowsAndKeepsAHiddenSelectedItemSelected()
    {
        var viewModel = new LibraryViewModel(_engine);
        viewModel.Render(_engine.State! with { Library = Items });
        viewModel.Selection.SelectAll();

        viewModel.List.SearchText = "CLIP";

        Assert.Equal(["Clip 10.mp4", "clip 2.mp4"], viewModel.List.Entries.OfType<LibraryRow>().Select(r => r.Name));
        Assert.Equal(("4 selected", "(2 hidden by filters)"), (viewModel.Actions.SelectionText, viewModel.Selection.HiddenText));
        Assert.Null(viewModel.List.NoResultsText);

        viewModel.List.SearchText = "zzz";
        Assert.Equal("No items match “zzz”.", viewModel.List.NoResultsText);
        viewModel.List.ClearSearchCommand.Execute(null);
        Assert.Equal(4, viewModel.List.Entries.OfType<LibraryRow>().Count(r => r.IsSelected));
    }

    [Fact]
    public void SortAndGroupByAreRememberedAndAnOlderFileLoadsWithTheDefaults()
    {
        var folder = Directory.CreateTempSubdirectory("rb-library-view-").FullName;
        try
        {
            var file = Path.Combine(folder, "library-view.json");
            var viewModel = new LibraryViewModel(_engine, viewStateFile: file);
            (viewModel.List.GroupBy, viewModel.List.Sort, viewModel.List.SearchText) = (LibraryGrouping.None, LibrarySort.LargestFirst, "kept?");

            var reopened = new LibraryViewModel(_engine, viewStateFile: file);
            Assert.Equal((LibraryGrouping.None, LibrarySort.LargestFirst, ""), (reopened.List.GroupBy, reopened.List.Sort, reopened.List.SearchText));

            // A file written before the sort existed has no sort.
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
