namespace Encore.App.Library;

/// <summary>The shell actions on Library items, given absolute paths; the App never opens the files itself. UI thread.</summary>
internal interface ILibraryShell
{
    /// <summary>Opens the file in its default player.</summary>
    void Open(string path);

    /// <summary>Opens a folder in one Explorer window with the given files (absolute paths in it) selected.</summary>
    void ShowInExplorer(string folder, IReadOnlyList<string> files);

    /// <summary>Opens <paramref name="folder"/> in Explorer.</summary>
    void OpenFolder(string folder);

    /// <summary>Puts every file on the clipboard as one file drop, so they paste as several attachments.</summary>
    void Copy(IReadOnlyList<string> paths);

    /// <summary>Copies the files into <paramref name="folder"/> with the shell's own progress and conflict dialogs.</summary>
    void CopyToFolder(IReadOnlyList<string> paths, string folder);
}
