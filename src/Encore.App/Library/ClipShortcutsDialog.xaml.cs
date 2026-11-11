using System.Windows;

namespace Encore.App.Library;

/// <summary>The help dialog that lists the clip creator's keyboard shortcuts.</summary>
internal partial class ClipShortcutsDialog
{
    internal ClipShortcutsDialog() => InitializeComponent();
    private void CloseHelp(object sender, RoutedEventArgs e) => DialogResult = false;
}
