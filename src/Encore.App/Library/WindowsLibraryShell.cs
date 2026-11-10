using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Encore.Platform.Desktop;

namespace Encore.App.Library;

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
