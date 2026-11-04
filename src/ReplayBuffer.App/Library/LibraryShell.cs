using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Library;

/// <summary>
/// The shell actions on a Library item (story 7.6), each given the item's absolute path; the App never
/// opens the file itself (AD-1). A failure throws. Called on the UI thread.
/// </summary>
internal interface ILibraryShell
{
    /// <summary>Opens the file in its default player.</summary>
    void Open(string path);

    /// <summary>Opens the file's folder in Explorer with the file selected.</summary>
    void ShowInExplorer(string path);

    /// <summary>Puts the file on the clipboard as a file drop, so it pastes as an attachment.</summary>
    void Copy(string path);
}

/// <summary>The Windows shell: ShellExecute <c>open</c>, Explorer's select, and the clipboard.</summary>
internal sealed class WindowsLibraryShell : ILibraryShell
{
    public void Open(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
    }

    public void ShowInExplorer(string path)
    {
        if (!ShellFolder.SelectInExplorer(path))
        {
            throw new IOException("Explorer couldn't show " + path);
        }
    }

    public void Copy(string path) => Clipboard.SetDataObject(LibraryClipboard.Create(path), copy: true);
}

/// <summary>The clipboard data of a copied Library item.</summary>
internal static class LibraryClipboard
{
    /// <summary>The shell's preferred-drop-effect format: a paste copies rather than moves.</summary>
    internal const string PreferredDropEffect = "Preferred DropEffect";

    private const int DropEffectCopy = 1;

    /// <summary>
    /// A data object holding exactly <paramref name="absolutePath"/> as a file drop (CF_HDROP) and
    /// <c>DROPEFFECT_COPY</c> as the preferred drop effect.
    /// </summary>
    public static DataObject Create(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { absolutePath });
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));
        return data;
    }
}
