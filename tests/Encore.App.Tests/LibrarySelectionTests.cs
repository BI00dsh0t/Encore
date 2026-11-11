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
        var viewModel = NewViewModel();
        viewModel.Render(State(Item(@"A\a1.mp4", 1), Item(@"A\a2.mp4", 2), Item(@"B\b1.mp4", 3)));
        Assert.Equal(["# A", "a1.mp4", "a2.mp4", "# B", "b1.mp4"], Shown(viewModel));

        viewModel.Selection.Click(Row(viewModel, "a2.mp4"), ctrl: false, shift: false);
        viewModel.Selection.Click(Row(viewModel, "b1.mp4"), ctrl: false, shift: true);
        Assert.Equal(["a2.mp4", "b1.mp4"], Selected(viewModel));

        viewModel.Selection.Click(Row(viewModel, "a1.mp4"), ctrl: true, shift: false);
        Assert.Equal(["a1.mp4", "a2.mp4", "b1.mp4"], Selected(viewModel));

        // The keyboard: Shift+Down twice from a1 extends across the header.
        viewModel.Selection.Click(Row(viewModel, "a1.mp4"), ctrl: false, shift: false);
        viewModel.Selection.MoveFocus(LibraryMove.Down, 1, ctrl: false, shift: true);
        viewModel.Selection.MoveFocus(LibraryMove.Down, 1, ctrl: false, shift: true);
        Assert.Equal(["a1.mp4", "a2.mp4", "b1.mp4"], Selected(viewModel));
        Assert.Equal("b1.mp4", viewModel.Selection.FocusedRow!.Name);
    }

    [Fact]
    public void TheSelectionSurvivesAFilterANewItemAndTheAppsRenameAndLosesARemovedItem()
    {
        var viewModel = NewViewModel();
        var a1 = Item(@"A\a1.mp4", 2);
        var a2 = Item(@"A\a2.mp4", 3);
        var b1 = Item(@"B\b1.mp4", 4);
        viewModel.Render(State(a1, a2, b1));
        viewModel.Selection.Click(Row(viewModel, "a1.mp4"), ctrl: false, shift: false);
        viewModel.Selection.Click(Row(viewModel, "b1.mp4"), ctrl: true, shift: false);

        // A filter hides b1 but keeps it; the actions act on a1 only.
        viewModel.List.GameFilter = viewModel.List.Games.Single(g => g.Name == "A").Key;
        Assert.Equal(["a1.mp4", "b1.mp4"], Selected(viewModel));
        Assert.Equal(("2 selected", "(1 hidden by filters)", true), (viewModel.Actions.SelectionText, viewModel.Selection.HiddenText, viewModel.Selection.IsSingle));
        viewModel.List.GameFilter = null;

        // A new item changes nothing.
        var c = Item(@"A\c.mp4", 1);
        viewModel.Render(State(c, a1, a2, b1));
        Assert.Equal(["a1.mp4", "b1.mp4"], Selected(viewModel));

        // The App's own rename carries the selection and the focus to the new path.
        viewModel.Selection.Click(Row(viewModel, "a1.mp4"), ctrl: false, shift: false);
        viewModel.RenameCommand.Execute(null);
        Row(viewModel, "a1.mp4").RenameText = "best";
        Row(viewModel, "a1.mp4").CommitRenameCommand.Execute(null);
        viewModel.Render(State(c, a1 with { Path = @"A\best.mp4" }, a2, b1));
        Assert.Equal(["best.mp4"], Selected(viewModel));
        Assert.Equal("best.mp4", viewModel.Selection.FocusedRow!.Name);

        // A removed item leaves the selection, and the focus moves to the next shown row.
        viewModel.Selection.Click(Row(viewModel, "a2.mp4"), ctrl: true, shift: false);
        viewModel.Render(State(c, a1 with { Path = @"A\best.mp4" }, b1));
        Assert.Equal(["best.mp4"], Selected(viewModel));
        Assert.Equal("b1.mp4", viewModel.Selection.FocusedRow!.Name);
    }

    [Fact]
    public void BulkFavouriteSendsOnePerItemThatChanges()
    {
        var viewModel = NewViewModel();
        var items = new[] { Item(@"A\a1.mp4", 1) with { Favourite = true }, Item(@"A\a2.mp4", 2), Item(@"B\b1.mp4", 3) };
        viewModel.Render(State(items));
        viewModel.Selection.SelectAll();

        Assert.Equal("Favourite", viewModel.Actions.FavouriteLabel);
        viewModel.FavouriteSelectionCommand.Execute(null);
        Assert.Equal([new LibrarySetFavourite(@"A\a2.mp4", Mb, Now, true), new LibrarySetFavourite(@"B\b1.mp4", Mb, Now, true)], _engine.Sent);

        _engine.Sent.Clear();
        viewModel.Render(State([.. items.Select(i => i with { Favourite = true })]));
        Assert.Equal("Remove from Favourites", viewModel.Actions.FavouriteLabel);
        viewModel.FavouriteSelectionCommand.Execute(null);
        Assert.Equal(3, _engine.Sent.Cast<LibrarySetFavourite>().Count(s => !s.Favourite));
    }

    [Fact]
    public void BulkDeleteAsksOnceLeavesOutFavouritesAndFilesBeingSavedAndReportsARefusal()
    {
        var asked = new List<string>();
        var viewModel = new LibraryViewModel(_engine, shell: _shell, confirmDelete: question =>
        {
            asked.Add(question);
            return true;
        });
        viewModel.Render(State(
            Item(@"G\d1.mp4", 1),
            Item(@"G\fav.mp4", 2) with { Favourite = true },
            Item(@"G\d2.mp4", 3),
            Item(@"G\saving.mp4", 4) with { BeingWritten = true },
            Item(@"G\d3.mp4", 5)));
        viewModel.Selection.SelectAll();
        _engine.Reply = (_, args) =>
        {
            Assert.False(viewModel.DeleteCommand.CanExecute(null));
            return ((LibraryDelete)args).Path == @"G\d2.mp4"
                ? throw new ErrorCodeException(ErrorCodes.FileBeingWritten, "busy")
                : Empty.Value;
        };

        viewModel.DeleteCommand.Execute(null);

        Assert.Equal("Move 3 items (3.0 MB) to the Recycle Bin?\n1 Favourite and 1 file being saved are kept.", Assert.Single(asked));
        Assert.Equal([@"G\d1.mp4", @"G\d2.mp4", @"G\d3.mp4"], _engine.Sent.Cast<LibraryDelete>().Select(d => d.Path));
        Assert.Equal("Deleted 2 of 3. That file is still being written.", viewModel.Actions.ActionError);
        Assert.Equal("5 selected", viewModel.Actions.SelectionText);
    }

    [Fact]
    public void ARowStarredWhileABatchRunsIsNotDeleted()
    {
        var viewModel = new LibraryViewModel(_engine, shell: _shell, confirmDelete: _ => true);
        var items = new[] { Item(@"G\d1.mp4", 1), Item(@"G\d2.mp4", 2), Item(@"G\d3.mp4", 3) };
        viewModel.Render(State(items));
        viewModel.Selection.SelectAll();
        _engine.Reply = (_, _) =>
        {
            viewModel.Render(State(items[0], items[1], items[2] with { Favourite = true }));
            return Empty.Value;
        };

        viewModel.DeleteCommand.Execute(null);

        Assert.Equal([@"G\d1.mp4", @"G\d2.mp4"], _engine.Sent.Cast<LibraryDelete>().Select(d => d.Path));
        Assert.Null(viewModel.Actions.ActionError);
    }

    [Fact]
    public void DeletingASingleFavouriteAsksAndSendsNothing()
    {
        var asked = 0;
        var viewModel = new LibraryViewModel(_engine, shell: _shell, confirmDelete: _ => ++asked > 0);
        viewModel.Render(State(Item(@"A\fav.mp4", 1) with { Favourite = true }));
        viewModel.Selection.Click(viewModel.List.Rows[0], ctrl: false, shift: false);

        Assert.False(viewModel.DeleteCommand.CanExecute(null));
        Assert.NotNull(viewModel.Actions.DeleteToolTip);
        viewModel.Actions.DeleteSelection();

        Assert.Equal("Favourites can't be deleted. Remove it from Favourites first.", viewModel.Actions.ActionError);
        Assert.Equal(0, asked);
        Assert.Empty(_engine.Sent);
    }

    [Fact]
    public void CopyIsOneFileDropOfTheShownWrittenFilesAndCopyToFolderCopiesTheSame()
    {
        var viewModel = new LibraryViewModel(_engine, shell: _shell, pickFolder: () => @"D:\Share");
        viewModel.Render(State(Item(@"A\a1.mp4", 1), Item(@"A\a2.mp4", 2), Item(@"A\saving.mp4", 3) with { BeingWritten = true }, Item(@"B\b1.mp4", 4)));
        viewModel.Selection.SelectAll();
        viewModel.List.GameFilter = viewModel.List.Games.Single(g => g.Name == "A").Key;

        viewModel.CopyCommand.Execute(null);
        viewModel.CopyToFolderCommand.Execute(null);

        var shown = Absolute(@"A\a1.mp4") + "|" + Absolute(@"A\a2.mp4");
        Assert.Equal([("copy", shown), (@"copy to D:\Share", shown)], _shell.Calls);
        Assert.Null(viewModel.Actions.ActionError);
    }

    [Fact]
    public void ShowInExplorerOpensOneWindowPerFolderAndTheLibraryFolderPastThree()
    {
        var viewModel = NewViewModel();
        viewModel.Render(State(Item(@"A\a1.mp4", 1), Item(@"A\a2.mp4", 2), Item(@"B\b1.mp4", 3)));
        viewModel.Selection.SelectAll();

        viewModel.ShowInExplorerCommand.Execute(null);
        Assert.Equal(
            [("show " + Absolute("A"), Absolute(@"A\a1.mp4") + "|" + Absolute(@"A\a2.mp4")), ("show " + Absolute("B"), Absolute(@"B\b1.mp4"))],
            _shell.Calls);

        _shell.Calls.Clear();
        viewModel.Render(State(Item(@"A\a1.mp4", 1), Item(@"B\b1.mp4", 2), Item(@"C\c1.mp4", 3), Item(@"D\d1.mp4", 4)));
        viewModel.Selection.SelectAll();
        viewModel.ShowInExplorerCommand.Execute(null);
        Assert.Equal([("folder", _folder)], _shell.Calls);
    }

    private LibraryViewModel NewViewModel() => new(_engine, shell: _shell);

    // An item of 1 MB whose file exists, the given hours old.
    private LibraryItem Item(string path, int hoursOld)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Absolute(path))!);
        File.WriteAllBytes(Absolute(path), [0]);
        return new LibraryItem(path, null, Mb, Now.AddHours(-hoursOld), false, Modified: Now);
    }

    private StateSnapshot State(params LibraryItem[] items) => _engine.State! with { LibraryFolder = _folder, Library = [.. items] };

    private string Absolute(string path) => Path.Combine(_folder, path);

    private static LibraryRow Row(LibraryViewModel viewModel, string name) => viewModel.List.Rows.Single(r => r.Name == name);

    private static List<string> Selected(LibraryViewModel viewModel) => [.. viewModel.List.Rows.Where(r => r.IsSelected).Select(r => r.Name).Order(StringComparer.Ordinal)];

    private static List<string> Shown(LibraryViewModel viewModel) =>
        [.. viewModel.List.Entries.Select(e => e is LibraryGroupHeader h ? "# " + h.Name : ((LibraryRow)e).Name)];
}
