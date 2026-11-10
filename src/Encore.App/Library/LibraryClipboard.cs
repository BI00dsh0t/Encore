using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace Encore.App.Library;

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
