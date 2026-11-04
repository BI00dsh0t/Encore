using System.IO;
using System.Windows;
using ReplayBuffer.App.Library;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class LibraryActionsTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly FakeEngineSession _engine = new();
    private readonly FakeShell _shell = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-library-actions-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ActionsPassTheAbsolutePathAndCopyWaitsUntilTheItemIsWritten()
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
        var page = new LibraryPage(_engine, shell: _shell);
        var writing = new LibraryItem(Clip, null, 0, DateTimeOffset.UnixEpoch, true);
        var state = _engine.State! with { LibraryFolder = _folder, Library = [writing] };

        page.Render(state);
        var row = page.Rows[0];
        var changed = 0;
        row.CopyCommand.CanExecuteChanged += (_, _) => changed++;
        Assert.False(row.CopyCommand.CanExecute(null));
        row.CopyCommand.Execute(null);
        row.OpenCommand.Execute(null);
        Assert.Equal([("open", absolute)], _shell.Calls);

        page.Render(state with { Library = [writing with { Size = 1, BeingWritten = false }] });
        Assert.Equal(1, changed);
        Assert.True(row.CopyCommand.CanExecute(null));
        row.ShowInExplorerCommand.Execute(null);
        row.CopyCommand.Execute(null);

        Assert.Equal([("open", absolute), ("show", absolute), ("copy", absolute)], _shell.Calls);
        Assert.Null(page.ActionError);
    }

    [Fact]
    public void AMissingFileOrAFailingShellShowsAShortError()
    {
        var page = new LibraryPage(_engine, shell: _shell);
        page.Render(_engine.State! with { LibraryFolder = _folder, Library = [new LibraryItem("gone.mp4", null, 1, DateTimeOffset.UnixEpoch, false)] });

        page.Rows[0].OpenCommand.Execute(null);
        Assert.Equal("That file no longer exists.", page.ActionError);
        Assert.True(page.HasActionError);
        Assert.Empty(_shell.Calls);

        File.WriteAllBytes(Path.Combine(_folder, "gone.mp4"), [0]);
        _shell.Fail = true;
        page.Rows[0].CopyCommand.Execute(null);
        Assert.Equal("Couldn't copy the file.", page.ActionError);

        _shell.Fail = false;
        page.Rows[0].OpenCommand.Execute(null);
        Assert.Null(page.ActionError);
    }

    [Fact]
    public void RenameSendsTheIdentityAndTheBaseNameAndShowsARefusal()
    {
        var item = new LibraryItem(Clip, null, 48, DateTimeOffset.UnixEpoch, false, Modified: DateTimeOffset.UnixEpoch.AddDays(1));
        var page = new LibraryPage(_engine, shell: _shell);
        page.Render(_engine.State! with { LibraryFolder = _folder, Library = [item] });
        var row = page.Rows[0];
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.FileNameTaken, "taken");

        row.RenameCommand.Execute(null);
        Assert.Equal((true, "clip"), (row.IsRenaming, row.RenameText));
        row.RenameText = "  best.MP4 ";
        row.CommitRenameCommand.Execute(null);

        Assert.Equal(new LibraryRename(Clip, 48, item.Modified, "best"), Assert.Single(_engine.Sent));
        Assert.False(row.IsRenaming);
        Assert.Equal("A file with that name already exists.", page.ActionError);

        // Escape sends nothing; a success clears the error.
        row.RenameCommand.Execute(null);
        row.CancelRenameCommand.Execute(null);
        row.CommitRenameCommand.Execute(null);
        Assert.Single(_engine.Sent);
        _engine.Reply = (_, _) => Empty.Value;
        row.RenameCommand.Execute(null);
        row.CommitRenameCommand.Execute(null);
        Assert.Equal(2, _engine.Sent.Count);
        Assert.Null(page.ActionError);
    }

    [Fact]
    public void DeleteAsksFirstAndSendsOnlyOnYesAndBothWaitUntilTheItemIsWritten()
    {
        var answer = false;
        var asked = new List<string>();
        var page = new LibraryPage(_engine, shell: _shell, confirmDelete: name =>
        {
            asked.Add(name);
            return answer;
        });
        var writing = new LibraryItem(Clip, null, 0, DateTimeOffset.UnixEpoch, true);
        var state = _engine.State! with { LibraryFolder = _folder, Library = [writing] };
        page.Render(state);
        var row = page.Rows[0];
        Assert.False(row.RenameCommand.CanExecute(null));
        Assert.False(row.DeleteCommand.CanExecute(null));

        var written = writing with { Size = 48, BeingWritten = false, Modified = DateTimeOffset.UnixEpoch };
        page.Render(state with { Library = [written] });
        Assert.True(row.RenameCommand.CanExecute(null));
        _engine.Reply = (_, _) => Empty.Value;

        row.DeleteCommand.Execute(null);
        Assert.Empty(_engine.Sent);

        answer = true;
        row.DeleteCommand.Execute(null);
        Assert.Equal(["clip.mp4", "clip.mp4"], asked);
        Assert.Equal(new LibraryDelete(Clip, 48, written.Modified), Assert.Single(_engine.Sent));
    }

    [Fact]
    public void TheClipboardDataIsTheOnePathAsAFileDropToCopy() => Sta.Run(() =>
    {
        var path = Path.Combine(_folder, Clip);

        var data = LibraryClipboard.Create(path);

        Assert.Equal([path], data.GetFileDropList().Cast<string>());
        var effect = Assert.IsType<MemoryStream>(data.GetData(LibraryClipboard.PreferredDropEffect));
        Assert.Equal([1, 0, 0, 0], effect.ToArray());
    });

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void CopyPutsTheFileOnTheRealClipboard() => Sta.Run(() =>
    {
        var path = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(path, [0]);

        new WindowsLibraryShell().Copy(path);

        Assert.Equal([path], Clipboard.GetFileDropList().Cast<string>());
        var effect = Assert.IsType<MemoryStream>(Clipboard.GetData(LibraryClipboard.PreferredDropEffect));
        Assert.Equal([1, 0, 0, 0], effect.ToArray());
    });

    private sealed class FakeShell : ILibraryShell
    {
        public List<(string Action, string Path)> Calls { get; } = [];

        public bool Fail { get; set; }

        public void Open(string path) => Record("open", path);

        public void ShowInExplorer(string path) => Record("show", path);

        public void Copy(string path) => Record("copy", path);

        private void Record(string action, string path)
        {
            if (Fail)
            {
                throw new IOException("shell failed");
            }

            Calls.Add((action, path));
        }
    }
}
