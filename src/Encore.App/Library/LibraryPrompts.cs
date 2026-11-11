using System.Windows;

namespace Encore.App.Library;

/// <summary>The questions the Library asks in a dialog, over the window of the view that asks.</summary>
internal static class LibraryPrompts
{
    public static bool ConfirmDelete(FrameworkElement? view, string question) =>
        Prompts.Ask(view, question, "Delete");

    /// <summary>The folder the user picks to copy files into, or null when they cancel.</summary>
    public static string? PickFolder(FrameworkElement? view)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Copy to folder" };
        var owner = view is null ? null : Window.GetWindow(view);
        var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return picked == true ? dialog.FolderName : null;
    }
}
