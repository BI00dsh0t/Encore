using Windows.Win32;
using Windows.Win32.UI.Shell.Common;

namespace Encore.Platform.Desktop;

/// <summary>Explorer's "show in folder". Call it on a thread where COM is initialized, such as the UI thread.</summary>
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

    /// <summary>Opens the folder in one Explorer window with each file selected; a file the shell can't parse is left out.</summary>
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
