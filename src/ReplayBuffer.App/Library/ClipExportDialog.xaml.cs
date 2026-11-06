using System.Windows;

namespace ReplayBuffer.App.Library;

/// <summary>Owned, themed options. Edits are the creator configuration; dismissal never exports.</summary>
internal partial class ClipExportDialog
{
    public ClipExportDialog(ClipCreatorPage page)
    {
        InitializeComponent();
        DataContext = page;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }
    internal static event Action<ClipExportDialog>? Showing;
    internal bool Ask(Window? owner)
    {
        if (owner is { IsVisible: true }) { Owner = owner; MaxHeight = Math.Max(240, owner.ActualHeight - 32); }
        else { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        Showing?.Invoke(this);
        return ShowDialog() == true;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (DataContext is ClipCreatorPage { CanSave: true }) { DialogResult = true; }
    }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
