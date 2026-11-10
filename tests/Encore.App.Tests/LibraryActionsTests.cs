using System.IO;
using System.Windows;
using Encore.App.Library;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class LibraryActionsTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly FakeEngineSession _engine = new();
    private readonly FakeLibraryShell _shell = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-library-actions-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ActionsPassTheAbsolutePathAndCopyWaitsUntilTheItemIsWritten()
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
        var viewModel = new LibraryViewModel(_engine, shell: _shell);
        var writing = new LibraryItem(Clip, null, 0, DateTimeOffset.UnixEpoch, true);
        var state = _engine.State! with { LibraryFolder = _folder, Library = [writing] };

        viewModel.Render(state);
        viewModel.Click(viewModel.Rows[0], ctrl: false, shift: false);
        var changed = 0;
        viewModel.CopyCommand.CanExecuteChanged += (_, _) => changed++;
        Assert.False(viewModel.CopyCommand.CanExecute(null));
        viewModel.CopyCommand.Execute(null);
        viewModel.OpenCommand.Execute(null);
        Assert.Equal([("open", absolute)], _shell.Calls);

        viewModel.Render(state with { Library = [writing with { Size = 1, BeingWritten = false }] });
        Assert.True(changed > 0);
        Assert.True(viewModel.CopyCommand.CanExecute(null));
        viewModel.ShowInExplorerCommand.Execute(null);
        viewModel.CopyCommand.Execute(null);

        Assert.Equal([("open", absolute), ("show " + Path.GetDirectoryName(absolute), absolute), ("copy", absolute)], _shell.Calls);
        Assert.Null(viewModel.ActionError);
    }

    [Fact]
    public void AMissingFileOrAFailingShellShowsAShortError()
    {
        var viewModel = new LibraryViewModel(_engine, shell: _shell);
        viewModel.Render(_engine.State! with { LibraryFolder = _folder, Library = [new LibraryItem("gone.mp4", null, 1, DateTimeOffset.UnixEpoch, false)] });
        viewModel.Click(viewModel.Rows[0], ctrl: false, shift: false);

        viewModel.OpenCommand.Execute(null);
        Assert.Equal("That file no longer exists.", viewModel.ActionError);
        Assert.True(viewModel.HasActionError);
        Assert.Empty(_shell.Calls);

        File.WriteAllBytes(Path.Combine(_folder, "gone.mp4"), [0]);
        _shell.Fail = true;
        viewModel.CopyCommand.Execute(null);
        Assert.Equal("Couldn't copy the file.", viewModel.ActionError);

        _shell.Fail = false;
        viewModel.OpenCommand.Execute(null);
        Assert.Null(viewModel.ActionError);
    }

    [Fact]
    public void RenameSendsTheIdentityAndTheBaseNameAndShowsARefusal()
    {
        var item = new LibraryItem(Clip, null, 48, DateTimeOffset.UnixEpoch, false, Modified: DateTimeOffset.UnixEpoch.AddDays(1));
        var viewModel = new LibraryViewModel(_engine, shell: _shell);
        viewModel.Render(_engine.State! with { LibraryFolder = _folder, Library = [item] });
        var row = viewModel.Rows[0];
        viewModel.Click(row, ctrl: false, shift: false);
        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.FileNameTaken, "taken");

        viewModel.RenameCommand.Execute(null);
        Assert.Equal((true, "clip"), (row.IsRenaming, row.RenameText));
        row.RenameText = "  best.MP4 ";
        row.CommitRenameCommand.Execute(null);

        Assert.Equal(new LibraryRename(Clip, 48, item.Modified, "best"), Assert.Single(_engine.Sent));
        Assert.False(row.IsRenaming);
        Assert.Equal("A file with that name already exists.", viewModel.ActionError);

        // Escape sends nothing; a success clears the error.
        viewModel.RenameCommand.Execute(null);
        row.CancelRenameCommand.Execute(null);
        row.CommitRenameCommand.Execute(null);
        Assert.Single(_engine.Sent);
        _engine.Reply = (_, _) => Empty.Value;
        viewModel.RenameCommand.Execute(null);
        row.CommitRenameCommand.Execute(null);
        Assert.Equal(2, _engine.Sent.Count);
        Assert.Null(viewModel.ActionError);
    }

    [Fact]
    public void DeleteAsksFirstAndSendsOnlyOnYesAndBothWaitUntilTheItemIsWritten()
    {
        var answer = false;
        var asked = new List<string>();
        var viewModel = new LibraryViewModel(_engine, shell: _shell, confirmDelete: question =>
        {
            asked.Add(question);
            return answer;
        });
        var writing = new LibraryItem(Clip, null, 0, DateTimeOffset.UnixEpoch, true);
        var state = _engine.State! with { LibraryFolder = _folder, Library = [writing] };
        viewModel.Render(state);
        viewModel.Click(viewModel.Rows[0], ctrl: false, shift: false);
        Assert.False(viewModel.RenameCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCommand.CanExecute(null));

        var written = writing with { Size = 48, BeingWritten = false, Modified = DateTimeOffset.UnixEpoch };
        viewModel.Render(state with { Library = [written] });
        Assert.True(viewModel.RenameCommand.CanExecute(null));
        _engine.Reply = (_, _) => Empty.Value;

        viewModel.DeleteCommand.Execute(null);
        Assert.Empty(_engine.Sent);

        answer = true;
        viewModel.DeleteCommand.Execute(null);
        Assert.Equal(["Move “clip.mp4” to the Recycle Bin?", "Move “clip.mp4” to the Recycle Bin?"], asked);
        Assert.Equal(new LibraryDelete(Clip, 48, written.Modified), Assert.Single(_engine.Sent));
    }

    [Fact]
    public void TheClipboardDataIsEveryPathInOneFileDropToCopy() => Sta.Run(() =>
    {
        string[] paths = [Path.Combine(_folder, Clip), Path.Combine(_folder, "other.mp4")];

        var data = LibraryClipboard.Create(paths);

        Assert.Equal(paths, data.GetFileDropList().Cast<string>());
        var effect = Assert.IsType<MemoryStream>(data.GetData(LibraryClipboard.PreferredDropEffect));
        Assert.Equal([1, 0, 0, 0], effect.ToArray());
    });

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void CopyPutsTheFileOnTheRealClipboard() => Sta.Run(() =>
    {
        var path = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(path, [0]);

        new WindowsLibraryShell().Copy([path]);

        Assert.Equal([path], Clipboard.GetFileDropList().Cast<string>());
        var effect = Assert.IsType<MemoryStream>(Clipboard.GetData(LibraryClipboard.PreferredDropEffect));
        Assert.Equal([1, 0, 0, 0], effect.ToArray());
    });
}

/// <summary>Records each shell call as (action, its paths joined by '|'); launches nothing.</summary>
internal sealed class FakeLibraryShell : ILibraryShell
{
    public List<(string Action, string Paths)> Calls { get; } = [];

    public bool Fail { get; set; }

    public void Open(string path) => Record("open", [path]);

    public void ShowInExplorer(string folder, IReadOnlyList<string> files) => Record("show " + folder, files);

    public void OpenFolder(string folder) => Record("folder", [folder]);

    public void Copy(IReadOnlyList<string> paths) => Record("copy", paths);

    public void CopyToFolder(IReadOnlyList<string> paths, string folder) => Record("copy to " + folder, paths);

    private void Record(string action, IReadOnlyList<string> paths)
    {
        if (Fail)
        {
            throw new IOException("shell failed");
        }

        Calls.Add((action, string.Join('|', paths)));
    }
}
