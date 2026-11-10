using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Encore.Platform.Desktop;

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

/// <summary>The Windows shell: ShellExecute <c>open</c>, Explorer's select, the clipboard and the shell's file copy.</summary>
internal sealed class WindowsLibraryShell : ILibraryShell
{
    public void Open(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
    }

    public void ShowInExplorer(string folder, IReadOnlyList<string> files)
    {
        if (!ShellFolder.SelectInExplorer(folder, files))
        {
            throw new IOException("Explorer couldn't show " + folder);
        }
    }

    public void OpenFolder(string folder)
    {
        using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
    }

    public void Copy(IReadOnlyList<string> paths) => Clipboard.SetDataObject(LibraryClipboard.Create(paths), copy: true);

    // The shell's copy blocks until its dialogs close, so it runs on a thread of its own, owned by the main window.
    public void CopyToFolder(IReadOnlyList<string> paths, string folder)
    {
        var window = Application.Current?.MainWindow;
        var owner = window is null ? 0 : new WindowInteropHelper(window).Handle;
        var files = paths.ToArray();
        var thread = new Thread(() =>
        {
            try
            {
                ShellFileCopy.Copy(files, folder, owner);
            }
#pragma warning disable CA1031 // The shell has shown its own error dialog; nothing is left to report.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        })
        {
            IsBackground = true,
            Name = "Library copy to folder",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}

/// <summary>The clipboard data of copied Library items.</summary>
internal static class LibraryClipboard
{
    /// <summary>The shell's preferred-drop-effect format: a paste copies rather than moves.</summary>
    internal const string PreferredDropEffect = "Preferred DropEffect";

    private const int DropEffectCopy = 1;

    /// <summary>A data object holding exactly these paths as one file drop, with copy as the preferred drop effect.</summary>
    public static DataObject Create(IReadOnlyList<string> absolutePaths)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);
        ArgumentOutOfRangeException.ThrowIfZero(absolutePaths.Count);
        var files = new StringCollection();
        foreach (var path in absolutePaths)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            files.Add(path);
        }

        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));
        return data;
    }
}
