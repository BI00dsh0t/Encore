using System.Globalization;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>What the Library's commands do with the shown selected rows, and the error line when one fails. UI thread.</summary>
internal sealed class LibraryFileActions : Observable
{
    private const int MaxExplorerWindows = 3;

    // A delete of more items than this shows its progress in the command bar.
    private const int ProgressFrom = 10;

    private readonly IEngineSession _engine;
    private readonly ILibraryShell _shell;
    private readonly LibraryList _list;
    private readonly LibrarySelection _selection;
    private readonly Func<string, bool> _confirmDelete;
    private readonly Func<string?> _pickFolder;
    private string? _libraryFolder;
    private string? _actionError;
    private string? _actionErrorPath;
    private bool _deleting;
    private int _deleteDone;
    private int _deleteTotal;

    public LibraryFileActions(
        IEngineSession engine,
        ILibraryShell shell,
        LibraryList list,
        LibrarySelection selection,
        Func<string, bool> confirmDelete,
        Func<string?> pickFolder)
    {
        _engine = engine;
        _shell = shell;
        _list = list;
        _selection = selection;
        _confirmDelete = confirmDelete;
        _pickFolder = pickFolder;
        _selection.Changed += OnSelectionChanged;
    }

    /// <summary>The last row action's failure, shown under the filters; null once one succeeds or its row leaves the Library.</summary>
    public string? ActionError
    {
        get => _actionError;
        private set
        {
            if (Set(ref _actionError, value))
            {
                OnPropertyChanged(nameof(HasActionError));
            }
        }
    }

    public bool HasActionError => ActionError is not null;

    /// <summary>Whether a delete is sending its items; every command waits for it.</summary>
    public bool IsDeleting => _deleting;

    /// <summary>"{n} selected" for the selected items still in the Library; "Deleting {k} of {n}…" during a large delete.</summary>
    public string SelectionText => _deleting && _deleteTotal > ProgressFrom
        ? string.Create(CultureInfo.InvariantCulture, $"Deleting {_deleteDone} of {_deleteTotal}…")
        : string.Create(CultureInfo.InvariantCulture, $"{_selection.Count} selected");

    /// <summary>"Remove from Favourites" when every shown selected item is one; else "Favourite".</summary>
    public string FavouriteLabel =>
        Selected.Count > 0 && Selected.All(row => row.IsFavourite) ? "Remove from Favourites" : "Favourite";

    /// <summary>Why Delete is disabled, when every shown selected item is a Favourite or being written; else null.</summary>
    public string? DeleteToolTip =>
        Selected.Count > 0 && !Selected.Any(LibraryDeleteText.IsDeletable) ? LibraryDeleteText.NotDeletable(Selected) : null;

    private IReadOnlyList<LibraryRow> Selected => _selection.ShownSelected;

    /// <summary>Takes the folder the engine lists, which the rows' paths are relative to.</summary>
    public void ShowFolder(string? libraryFolder) => _libraryFolder = libraryFolder;

    /// <summary>After the list read a snapshot: an error about a row that left the Library goes too.</summary>
    public void ForgetErrorOfMissingRow()
    {
        if (_actionErrorPath is { } failed && !_list.Contains(failed))
        {
            ClearError();
        }
    }

    public void ClearError()
    {
        _actionErrorPath = null;
        ActionError = null;
    }

    /// <summary>Shows a failure that belongs to no new row; the row the line follows stays as it was.</summary>
    public void ShowError(string message) => ActionError = message;

    /// <summary>The rows' absolute paths whose files still exist; null, with the error shown, when none does.</summary>
    public List<string>? Existing(IReadOnlyList<LibraryRow> rows)
    {
        // Only existence is checked, never the contents.
        var paths = rows.Select(Absolute).OfType<string>().Where(System.IO.File.Exists).ToList();
        if (paths.Count == 0)
        {
            Fail(rows[0].Path, "That file no longer exists.");
            return null;
        }

        return paths;
    }

    /// <summary>Hands a file or link to the shell for the row at <paramref name="rowPath"/>; a failure shows the message.</summary>
    public void OpenInShell(string rowPath, string target, string failure) => RunShell(rowPath, () => _shell.Open(target), failure);

    /// <summary>Open in default player: hands the one selected item to the shell, even while it is being written.</summary>
    public void OpenSelection()
    {
        if (Selected is [var row] && Existing([row]) is [var path])
        {
            OpenInShell(row.Path, path, "Couldn't open the file.");
        }
    }

    /// <summary>Puts the shown selected items that are written on the clipboard as one file drop.</summary>
    public void CopySelection()
    {
        var rows = Selected.Where(row => !row.IsLocked).ToList();
        if (rows.Count > 0 && Existing(rows) is { } paths)
        {
            RunShell(rows[0].Path, () => _shell.Copy(paths), paths.Count == 1 ? "Couldn't copy the file." : "Couldn't copy the files.");
        }
    }

    /// <summary>Copies the shown selected items that are written into a picked folder, through the shell.</summary>
    public void CopySelectionToFolder()
    {
        var rows = Selected.Where(row => !row.IsLocked).ToList();
        if (rows.Count > 0 && Existing(rows) is { } paths && _pickFolder() is { } folder)
        {
            RunShell(rows[0].Path, () => _shell.CopyToFolder(paths, folder), "Couldn't copy the files.");
        }
    }

    /// <summary>One Explorer window per folder with its files selected; past three folders, the Library's folder.</summary>
    public void ShowSelectionInExplorer()
    {
        var rows = Selected.ToList();
        if (rows.Count == 0 || Existing(rows) is not { } paths)
        {
            return;
        }

        var folders = paths
            .GroupBy(path => System.IO.Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Folder: group.Key, Files: (IReadOnlyList<string>)group.ToList()))
            .ToList();
        RunShell(
            rows[0].Path,
            () => ShowInExplorer(folders),
            paths.Count == 1 ? "Couldn't show the file." : "Couldn't show the files.");
    }

    /// <summary>Starts the inline rename of the one shown selected item.</summary>
    public void RenameSelection()
    {
        if (Selected is [var row])
        {
            row.RenameCommand.Execute(null);
        }
    }

    /// <summary>Toggles a row's Favourite; the star follows the snapshot, never this send.</summary>
    public async Task ToggleFavouriteAsync(LibraryRow row)
    {
        // The row's identity as last shown.
        if (row.Item is { } item)
        {
            await SendFavouriteAsync(item, !item.Favourite);
        }
    }

    /// <summary>Stars every shown selected item, or unstars them all when all are Favourites; one send per item that changes.</summary>
    public async Task FavouriteSelectionAsync()
    {
        var rows = Selected.ToList();
        var favourite = !rows.All(row => row.IsFavourite);
        var changing = rows.Where(row => row.IsFavourite != favourite).Select(row => row.Item).OfType<LibraryItem>().ToList();
        foreach (var item in changing)
        {
            await SendFavouriteAsync(item, favourite);
        }
    }

    /// <summary>Sends a row's rename; the row and the selection follow the snapshot to the new path.</summary>
    public async Task RenameAsync(LibraryRow row, string text)
    {
        // The engine renames by the identity last shown.
        if (row.Item is not { } item)
        {
            return;
        }

        var name = BaseName(text);
        _selection.ExpectRename(item.Path, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path) ?? "", name + ".mp4"));
        try
        {
            await _engine.SendAsync(LibraryMessages.Rename, new LibraryRename(item.Path, item.Size, item.Modified, name));
            ClearError();
        }
        catch (ErrorCodeException e)
        {
            _selection.ForgetRename(item.Path);
            Fail(row.Path, LibraryDeleteText.Refusal(e.Code, "Couldn't rename the file."));
        }
    }

    /// <summary>Delete: one confirmation for the selected items that aren't Favourites or being written, then one delete each.</summary>
    public void DeleteSelection()
    {
        if (_deleting || Selected.Count == 0)
        {
            return;
        }

        var shown = Selected.ToList();
        var deletable = shown.Where(LibraryDeleteText.IsDeletable).ToList();
        if (deletable.Count == 0)
        {
            Fail(shown[0].Path, LibraryDeleteText.NotDeletable(shown));
            return;
        }

        if (_confirmDelete(LibraryDeleteText.Question(shown, deletable)))
        {
            _ = DeleteAsync(deletable.ConvertAll(row => row.Item!));
        }
    }

    /// <summary>The base name a rename sends: trimmed, without one trailing <c>.mp4</c>.</summary>
    private static string BaseName(string text)
    {
        var name = text.Trim();
        return name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(FavouriteLabel));
        OnPropertyChanged(nameof(DeleteToolTip));
    }

    private string? Absolute(LibraryRow row) =>
        string.IsNullOrEmpty(_libraryFolder) ? null : System.IO.Path.Combine(_libraryFolder, row.Path);

    // One shell call: a success clears the error, any failure shows the short message.
    private void RunShell(string rowPath, Action action, string failure)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Any shell failure is shown as a short error, never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            Fail(rowPath, failure);
            return;
        }

        ClearError();
    }

    private void ShowInExplorer(List<(string Folder, IReadOnlyList<string> Files)> folders)
    {
        if (folders.Count > MaxExplorerWindows)
        {
            _shell.OpenFolder(_libraryFolder!);
            return;
        }

        foreach (var (folder, files) in folders)
        {
            _shell.ShowInExplorer(folder, files);
        }
    }

    private void Fail(string? path, string message)
    {
        _actionErrorPath = path;
        ActionError = message;
    }

    private async Task SendFavouriteAsync(LibraryItem item, bool favourite)
    {
        try
        {
            await _engine.SendAsync(LibraryMessages.SetFavourite, new LibrarySetFavourite(item.Path, item.Size, item.Modified, favourite));
        }
        catch (ErrorCodeException)
        {
            // A stale identity or a lost engine; the snapshot carries the truth.
        }
    }

    // One send at a time in shown order, by the identity as shown; the rows leave as the snapshot says.
    private async Task DeleteAsync(List<LibraryItem> items)
    {
        _deleting = true;
        _deleteTotal = items.Count;
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        string? firstFailed = null;
        var deleted = 0;
        var starred = 0;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                _deleteDone = i + 1;
                _selection.Refresh();
                var item = items[i];

                // A row starred while the batch runs is a Favourite now, and a Favourite is never deleted.
                if (_list.Find(item.Path) is { IsFavourite: true })
                {
                    starred++;
                }
                else if (await TryDeleteAsync(item) is { } code)
                {
                    failures[code] = failures.GetValueOrDefault(code) + 1;
                    firstFailed ??= item.Path;
                }
                else
                {
                    deleted++;
                }
            }
        }
        finally
        {
            _deleting = false;
            _selection.Refresh();
        }

        if (failures.Count == 0)
        {
            ClearError();
            return;
        }

        // The most common reason wins; a tie goes to the first seen.
        var commonest = failures.MaxBy(failure => failure.Value).Key;
        Fail(firstFailed, LibraryDeleteText.Failure(commonest, items.Count - starred, deleted));
    }

    // Sends one delete; the engine's error code when it refuses, else null.
    private async Task<string?> TryDeleteAsync(LibraryItem item)
    {
        try
        {
            await _engine.SendAsync(LibraryMessages.Delete, new LibraryDelete(item.Path, item.Size, item.Modified));
            return null;
        }
        catch (ErrorCodeException e)
        {
            return e.Code;
        }
    }
}
