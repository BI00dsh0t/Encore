using Windows.Win32;
using Windows.Win32.UI.Shell.Common;

namespace ReplayBuffer.Platform.Desktop;

/// <summary>
/// Explorer's "show in folder" (story 7.6): opens the file's folder with the file selected. The shell
/// gets only the path (AD-1). Call it on a thread where COM is initialized, such as the UI thread.
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
            fixed (char* name = path)
            {
                if (PInvoke.SHParseDisplayName(name, null, &pidl, 0, null).Failed || pidl is null)
                {
                    return false;
                }
            }

            // A full pidl and no children selects that item in its parent folder.
            return PInvoke.SHOpenFolderAndSelectItems(pidl, 0, null, 0).Succeeded;
        }
        finally
        {
            if (pidl is not null)
            {
                PInvoke.ILFree(pidl);
            }
        }
    }
}
