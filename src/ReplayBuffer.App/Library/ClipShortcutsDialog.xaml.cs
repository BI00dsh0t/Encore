using System.Windows;

namespace ReplayBuffer.App.Library;

internal partial class ClipShortcutsDialog
{
    internal ClipShortcutsDialog() => InitializeComponent();
    private void CloseHelp(object sender, RoutedEventArgs e) => DialogResult = false;
}
