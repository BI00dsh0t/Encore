using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Encore.App.Settings;

/// <summary>The Windows dialogs the folder pickers open, owned by the Main App window.</summary>
internal static class FolderDialogs
{
    /// <summary>Asks for a folder, opened at <paramref name="initial"/> when it exists; null when cancelled.</summary>
    public static string? ChooseFolder(string title, string initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initial.Length > 0 && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }

    /// <summary>Asks for an existing file that matches the filter; null when cancelled.</summary>
    public static string? ChooseFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}
