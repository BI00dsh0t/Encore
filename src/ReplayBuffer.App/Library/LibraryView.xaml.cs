using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ReplayBuffer.App.Library;

internal partial class LibraryView
{
    public LibraryView()
    {
        InitializeComponent();
    }

    // The inline editor takes focus with its text selected once shown; after the context menu has closed.
    private void OnRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && e.NewValue is true)
        {
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    // Leaving the editor commits it; a no-op once Enter or Escape has ended the edit.
    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryRow row })
        {
            row.CommitRenameCommand.Execute(null);
        }
    }
}
