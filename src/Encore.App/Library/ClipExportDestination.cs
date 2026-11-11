using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Encore.App.Library;

/// <summary>Asks where an export goes: a file for one output, or a parent folder for separate audio files.</summary>
internal static class ClipExportDestination
{
    /// <summary>The chosen path, or null when the builder cancels.</summary>
    internal static Task<string?> ChooseAsync(ClipCreatorViewModel viewModel)
    {
        var owner = Application.Current?.Windows.OfType<ClipExportDialog>().FirstOrDefault(window => window.IsVisible)
            ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        return Task.FromResult(viewModel.SeparateAudioFiles ? ChooseFolder(viewModel, owner) : ChooseFile(viewModel, owner));
    }

    private static string? ChooseFolder(ClipCreatorViewModel viewModel, Window? owner)
    {
        var folder = new OpenFolderDialog { Title = "Choose the parent folder for the new named MP3 directory", Multiselect = false };
        return folder.ShowDialog(owner) == true ? Path.Combine(folder.FolderName, viewModel.ExportName) : null;
    }

    private static string? ChooseFile(ClipCreatorViewModel viewModel, Window? owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = viewModel.SaveLabel,
            FileName = viewModel.ExportName + "." + viewModel.Format,
            Filter = viewModel.IsMp4 ? "MP4 video|*.mp4" : "MP3 audio|*.mp3",
            AddExtension = true,
            DefaultExt = viewModel.Format,
            OverwritePrompt = false,
            CheckFileExists = false,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
