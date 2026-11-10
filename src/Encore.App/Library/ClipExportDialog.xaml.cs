using System.Windows;

namespace Encore.App.Library;

/// <summary>Owned, themed options. Edits are the creator configuration; dismissal never exports.</summary>
internal partial class ClipExportDialog
{
    public ClipExportDialog(ClipCreatorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Raised as each dialog is about to show; tests answer through it.</summary>
    internal static event Action<ClipExportDialog>? Showing;

    /// <summary>Shows the dialog modally over its owner, or centred on the screen; true when the builder saved.</summary>
    internal bool Ask(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            MaxHeight = Math.Max(240, owner.ActualHeight - 32);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Showing?.Invoke(this);
        return ShowDialog() == true;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ClipCreatorViewModel { CanSave: true })
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
