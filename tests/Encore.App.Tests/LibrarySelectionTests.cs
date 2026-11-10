using System.IO;
using Encore.App.Library;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

/// <summary>The Library's selection, kept by the page, and the bulk actions on it.</summary>
[Collection(nameof(WpfCollection))]
public sealed class LibrarySelectionTests : IDisposable
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 20, 0, 0, TimeSpan.Zero);

    private readonly FakeEngineSession _engine = new() { Reply = (_, _) => Empty.Value };
    private readonly FakeLibraryShell _shell = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-library-selection-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ShiftAndCtrlSelectRangesAcrossAGroupHeader()
    {
        var page = Page();
        page.Render(State(Item(@"A\a1.mp4", 1), Item(@"A\a2.mp4", 2), Item(@"B\b1.mp4", 3)));
        Assert.Equal(["# A", "a1.mp4", "a2.mp4", "# B", "b1.mp4"], Shown(page));

        page.Click(Row(page, "a2.mp4"), ctrl: false, shift: false);
        page.Click(Row(page, "b1.mp4"), ctrl: false, shift: true);
        Assert.Equal(["a2.mp4", "b1.mp4"], Selected(page));

        page.Click(Row(page, "a1.mp4"), ctrl: true, shift: false);
        Assert.Equal(["a1.mp4", "a2.mp4", "b1.mp4"], Selected(page));

        // The keyboard: Shift+Down twice from a1 extends across the header.
        page.Click(Row(page, "a1.mp4"), ctrl: false, shift: false);
        page.MoveFocus(LibraryMove.Down, 1, ctrl: false, shift: true);
        page.MoveFocus(LibraryMove.Down, 1, ctrl: false, shift: true);
        Assert.Equal(["a1.mp4", "a2.mp4", "b1.mp4"], Selected(page));
        Assert.Equal("b1.mp4", page.FocusedRow!.Name);
    }

    [Fact]
    public void TheSelectionSurvivesAFilterANewItemAndTheAppsRenameAndLosesARemovedItem()
    {
        var page = Page();
        var a1 = Item(@"A\a1.mp4", 2);
        var a2 = Item(@"A\a2.mp4", 3);
        var b1 = Item(@"B\b1.mp4", 4);
        page.Render(State(a1, a2, b1));
        page.Click(Row(page, "a1.mp4"), ctrl: false, shift: false);
        page.Click(Row(page, "b1.mp4"), ctrl: true, shift: false);

        // A filter hides b1 but keeps it; the actions act on a1 only.
        page.GameFilter = page.Games.Single(g => g.Name == "A").Key;
        Assert.Equal(["a1.mp4", "b1.mp4"], Selected(page));
        Assert.Equal(("2 selected", "(1 hidden by filters)", true), (page.SelectionText, page.HiddenText, page.IsSingle));
        page.GameFilter = null;

        // A new item changes nothing.
        var c = Item(@"A\c.mp4", 1);
        page.Render(State(c, a1, a2, b1));
        Assert.Equal(["a1.mp4", "b1.mp4"], Selected(page));

        // The App's own rename carries the selection and the focus to the new path.
        page.Click(Row(page, "a1.mp4"), ctrl: false, shift: false);
        page.RenameCommand.Execute(null);
        Row(page, "a1.mp4").RenameText = "best";
        Row(page, "a1.mp4").CommitRenameCommand.Execute(null);
        page.Render(State(c, a1 with { Path = @"A\best.mp4" }, a2, b1));
        Assert.Equal(["best.mp4"], Selected(page));
        Assert.Equal("best.mp4", page.FocusedRow!.Name);

        // A removed item leaves the selection, and the focus moves to the next shown row.
        page.Click(Row(page, "a2.mp4"), ctrl: true, shift: false);
        page.Render(State(c, a1 with { Path = @"A\best.mp4" }, b1));
        Assert.Equal(["best.mp4"], Selected(page));
        Assert.Equal("b1.mp4", page.FocusedRow!.Name);
    }

    [Fact]
    public void BulkFavouriteSendsOnePerItemThatChanges()
    {
        var page = Page();
        var items = new[] { Item(@"A\a1.mp4", 1) with { Favourite = true }, Item(@"A\a2.mp4", 2), Item(@"B\b1.mp4", 3) };
        page.Render(State(items));
        page.SelectAll();

        Assert.Equal("Favourite", page.FavouriteLabel);
        page.FavouriteSelectionCommand.Execute(null);
        Assert.Equal([new LibrarySetFavourite(@"A\a2.mp4", Mb, Now, true), new LibrarySetFavourite(@"B\b1.mp4", Mb, Now, true)], _engine.Sent);

        _engine.Sent.Clear();
        page.Render(State([.. items.Select(i => i with { Favourite = true })]));
        Assert.Equal("Remove from Favourites", page.FavouriteLabel);
        page.FavouriteSelectionCommand.Execute(null);
        Assert.Equal(3, _engine.Sent.Cast<LibrarySetFavourite>().Count(s => !s.Favourite));
    }

    [Fact]
    public void BulkDeleteAsksOnceLeavesOutFavouritesAndFilesBeingSavedAndReportsARefusal()
    {
        var asked = new List<string>();
        var page = new LibraryPage(_engine, shell: _shell, confirmDelete: question =>
        {
            asked.Add(question);
            return true;
        });
        page.Render(State(
            Item(@"G\d1.mp4", 1),
            Item(@"G\fav.mp4", 2) with { Favourite = true },
            Item(@"G\d2.mp4", 3),
            Item(@"G\saving.mp4", 4) with { BeingWritten = true },
            Item(@"G\d3.mp4", 5)));
        page.SelectAll();
        _engine.Reply = (_, args) =>
        {
            Assert.False(page.DeleteCommand.CanExecute(null));
            return ((LibraryDelete)args).Path == @"G\d2.mp4"
                ? throw new ErrorCodeException(ErrorCodes.FileBeingWritten, "busy")
                : Empty.Value;
        };

        page.DeleteCommand.Execute(null);

        Assert.Equal("Move 3 items (3.0 MB) to the Recycle Bin?\n1 Favourite and 1 file being saved are kept.", Assert.Single(asked));
        Assert.Equal([@"G\d1.mp4", @"G\d2.mp4", @"G\d3.mp4"], _engine.Sent.Cast<LibraryDelete>().Select(d => d.Path));
        Assert.Equal("Deleted 2 of 3. That file is still being written.", page.ActionError);
        Assert.Equal("5 selected", page.SelectionText);
    }

    [Fact]
    public void ARowStarredWhileABatchRunsIsNotDeleted()
    {
        var page = new LibraryPage(_engine, shell: _shell, confirmDelete: _ => true);
        var items = new[] { Item(@"G\d1.mp4", 1), Item(@"G\d2.mp4", 2), Item(@"G\d3.mp4", 3) };
        page.Render(State(items));
        page.SelectAll();
        _engine.Reply = (_, _) =>
        {
            page.Render(State(items[0], items[1], items[2] with { Favourite = true }));
            return Empty.Value;
        };

        page.DeleteCommand.Execute(null);

        Assert.Equal([@"G\d1.mp4", @"G\d2.mp4"], _engine.Sent.Cast<LibraryDelete>().Select(d => d.Path));
        Assert.Null(page.ActionError);
    }

    [Fact]
    public void DeletingASingleFavouriteAsksAndSendsNothing()
    {
        var asked = 0;
        var page = new LibraryPage(_engine, shell: _shell, confirmDelete: _ => ++asked > 0);
        page.Render(State(Item(@"A\fav.mp4", 1) with { Favourite = true }));
        page.Click(page.Rows[0], ctrl: false, shift: false);

        Assert.False(page.DeleteCommand.CanExecute(null));
        Assert.NotNull(page.DeleteToolTip);
        page.DeleteSelection();

        Assert.Equal("Favourites can't be deleted. Remove it from Favourites first.", page.ActionError);
        Assert.Equal(0, asked);
        Assert.Empty(_engine.Sent);
    }

    [Fact]
    public void CopyIsOneFileDropOfTheShownWrittenFilesAndCopyToFolderCopiesTheSame()
    {
        var page = new LibraryPage(_engine, shell: _shell, pickFolder: () => @"D:\Share");
        page.Render(State(Item(@"A\a1.mp4", 1), Item(@"A\a2.mp4", 2), Item(@"A\saving.mp4", 3) with { BeingWritten = true }, Item(@"B\b1.mp4", 4)));
        page.SelectAll();
        page.GameFilter = page.Games.Single(g => g.Name == "A").Key;

        page.CopyCommand.Execute(null);
        page.CopyToFolderCommand.Execute(null);

        var shown = Absolute(@"A\a1.mp4") + "|" + Absolute(@"A\a2.mp4");
        Assert.Equal([("copy", shown), (@"copy to D:\Share", shown)], _shell.Calls);
        Assert.Null(page.ActionError);
    }

    [Fact]
    public void ShowInExplorerOpensOneWindowPerFolderAndTheLibraryFolderPastThree()
    {
        var page = Page();
        page.Render(State(Item(@"A\a1.mp4", 1), Item(@"A\a2.mp4", 2), Item(@"B\b1.mp4", 3)));
        page.SelectAll();

        page.ShowInExplorerCommand.Execute(null);
        Assert.Equal(
            [("show " + Absolute("A"), Absolute(@"A\a1.mp4") + "|" + Absolute(@"A\a2.mp4")), ("show " + Absolute("B"), Absolute(@"B\b1.mp4"))],
            _shell.Calls);

        _shell.Calls.Clear();
        page.Render(State(Item(@"A\a1.mp4", 1), Item(@"B\b1.mp4", 2), Item(@"C\c1.mp4", 3), Item(@"D\d1.mp4", 4)));
        page.SelectAll();
        page.ShowInExplorerCommand.Execute(null);
        Assert.Equal([("folder", _folder)], _shell.Calls);
    }

    private LibraryPage Page() => new(_engine, shell: _shell);

    // An item of 1 MB whose file exists, the given hours old.
    private LibraryItem Item(string path, int hoursOld)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Absolute(path))!);
        File.WriteAllBytes(Absolute(path), [0]);
        return new LibraryItem(path, null, Mb, Now.AddHours(-hoursOld), false, Modified: Now);
    }

    private StateSnapshot State(params LibraryItem[] items) => _engine.State! with { LibraryFolder = _folder, Library = [.. items] };

    private string Absolute(string path) => Path.Combine(_folder, path);

    private static LibraryRow Row(LibraryPage page, string name) => page.Rows.Single(r => r.Name == name);

    private static List<string> Selected(LibraryPage page) => [.. page.Rows.Where(r => r.IsSelected).Select(r => r.Name).Order(StringComparer.Ordinal)];

    private static List<string> Shown(LibraryPage page) =>
        [.. page.Entries.Select(e => e is LibraryGroupHeader h ? "# " + h.Name : ((LibraryRow)e).Name)];
}
