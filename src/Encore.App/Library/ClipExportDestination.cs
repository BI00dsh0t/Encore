using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Encore.App.Library;

/// <summary>Asks where an export goes: a file for one output, or a parent folder for separate audio files.</summary>
internal static class ClipExportDestination
{
    /// <summary>The chosen path, or null when the builder cancels.</summary>
    internal static Task<string?> ChooseAsync(ClipCreatorPage page)
    {
        var owner = Application.Current?.Windows.OfType<ClipExportDialog>().FirstOrDefault(window => window.IsVisible)
            ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        return Task.FromResult(page.SeparateAudioFiles ? ChooseFolder(page, owner) : ChooseFile(page, owner));
    }

    private static string? ChooseFolder(ClipCreatorPage page, Window? owner)
    {
        var folder = new OpenFolderDialog { Title = "Choose the parent folder for the new named MP3 directory", Multiselect = false };
        return folder.ShowDialog(owner) == true ? Path.Combine(folder.FolderName, page.ExportName) : null;
    }

    private static string? ChooseFile(ClipCreatorPage page, Window? owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = page.SaveLabel,
            FileName = page.ExportName + "." + page.Format,
            Filter = page.IsMp4 ? "MP4 video|*.mp4" : "MP3 audio|*.mp3",
            AddExtension = true,
            DefaultExt = page.Format,
            OverwritePrompt = false,
            CheckFileExists = false,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
