using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Encore.Platform.Desktop;

/// <summary>
/// Explorer's "show in folder" (stories 7.6 and 10.2): opens a folder with files selected. The shell
/// gets only the paths (AD-1). Call it on a thread where COM is initialized, such as the UI thread.
/// </summary>
public static unsafe class ShellFolder
{
    /// <summary>Opens <paramref name="path"/>'s folder in Explorer with it selected; false when the shell failed.</summary>
    public static bool SelectInExplorer(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        ITEMIDLIST* pidl = null;
        try
        {
            pidl = Parse(path);
            if (pidl is null)
            {
                return false;
            }

            // A full pidl and no children selects that item in its parent folder.
            return PInvoke.SHOpenFolderAndSelectItems(pidl, 0, null, 0).Succeeded;
        }
        finally
        {
            Free(pidl);
        }
    }

    /// <summary>
    /// Opens <paramref name="folder"/> in one Explorer window with each of <paramref name="files"/> (absolute
    /// paths inside it) selected; false when the shell failed. A file the shell can't parse is left out.
    /// </summary>
    public static bool SelectInExplorer(string folder, IReadOnlyList<string> files)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(files);

        ITEMIDLIST* parent = null;
        var full = new List<nint>(files.Count);
        try
        {
            parent = Parse(folder);
            if (parent is null)
            {
                return false;
            }

            foreach (var file in files)
            {
                if (Parse(file) is var pidl && pidl is not null)
                {
                    full.Add((nint)pidl);
                }
            }

            // Each child is the last id of its full pidl, relative to the folder.
            var children = stackalloc ITEMIDLIST*[Math.Max(1, full.Count)];
            for (var i = 0; i < full.Count; i++)
            {
                children[i] = PInvoke.ILFindLastID((ITEMIDLIST*)full[i]);
            }

            return PInvoke.SHOpenFolderAndSelectItems(parent, (uint)full.Count, full.Count == 0 ? null : children, 0).Succeeded;
        }
        finally
        {
            Free(parent);
            foreach (var pidl in full)
            {
                Free((ITEMIDLIST*)pidl);
            }
        }
    }

    private static ITEMIDLIST* Parse(string path)
    {
        ITEMIDLIST* pidl = null;
        fixed (char* name = path)
        {
            return PInvoke.SHParseDisplayName(name, null, &pidl, 0, null).Failed ? null : pidl;
        }
    }

    private static void Free(ITEMIDLIST* pidl)
    {
        if (pidl is not null)
        {
            PInvoke.ILFree(pidl);
        }
    }
}

/// <summary>
/// The Windows shell's own file copy (story 10.2): its progress, name-conflict and error dialogs, and an
/// undoable copy. The App never reads the files itself (AD-1). It blocks until the copy ends, so call it on
/// a background thread in a single-threaded apartment.
/// </summary>
public static unsafe class ShellFileCopy
{
    private const uint FoCopy = 0x2;
    private const ushort FofAllowUndo = 0x40;
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Copies <paramref name="files"/> (absolute paths) into <paramref name="folder"/>, with the shell's
    /// dialogs owned by <paramref name="owner"/> (a window handle, or 0). A cancel by the user returns
    /// normally; a failure throws <see cref="Win32Exception"/>.
    /// </summary>
    public static void Copy(IReadOnlyList<string> files, string folder, nint owner)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (files.Count == 0)
        {
            return;
        }

        // Both lists are double-null terminated: each path ends in a null, then one more ends the list.
        var from = string.Join('\0', files) + "\0\0";
        var to = folder + "\0\0";
        fixed (char* pFrom = from)
        fixed (char* pTo = to)
        {
            var operation = new SHFILEOPSTRUCTW
            {
                hwnd = new HWND(owner),
                wFunc = FoCopy,
                pFrom = pFrom,
                pTo = pTo,
                fFlags = FofAllowUndo,
            };
            var result = PInvoke.SHFileOperation(ref operation);
            if (result != 0 && result != ErrorCancelled && !operation.fAnyOperationsAborted)
            {
                throw new Win32Exception(result, $"The shell couldn't copy the files (0x{result:X}).");
            }
        }
    }
}
