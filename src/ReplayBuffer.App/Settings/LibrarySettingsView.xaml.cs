using System.Windows;
using Microsoft.Win32;

namespace ReplayBuffer.App.Settings;

internal partial class LibrarySettingsView
{
    public LibrarySettingsView()
    {
        InitializeComponent();
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LibrarySettingsPage page)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose the Output Folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            page.ChooseOutputFolder(dialog.FolderName);
        }
    }
}
