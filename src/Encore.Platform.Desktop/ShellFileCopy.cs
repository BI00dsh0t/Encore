using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Encore.Platform.Desktop;

/// <summary>The Windows shell's own file copy, with its dialogs and undo. It blocks, so call it on a background STA thread.</summary>
public static unsafe class ShellFileCopy
{
    private const uint FoCopy = 0x2;
    private const ushort FofAllowUndo = 0x40;
    private const int ErrorCancelled = 1223;

    /// <summary>Copies files into a folder; owner is a window handle or 0. A cancel by the user returns, a failure throws.</summary>
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
