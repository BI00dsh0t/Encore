using System.Windows;
using Microsoft.Win32;

namespace ReplayBuffer.App.Settings;

internal partial class GameProfilesView
{
    public GameProfilesView()
    {
        InitializeComponent();
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GameProfilesPage page)
        {
            return;
        }

        var dialog = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            page.AddPath = dialog.FileName;
        }
    }
}
