using Encore.App.Settings;

namespace Encore.App;

/// <summary>One navigation item: the main window's Library, or a section of the Settings window.</summary>
internal sealed class NavItem : Observable
{
    public NavItem(string title, SettingsViewModel? viewModel)
    {
        Title = title;
        ViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.HasUnsavedChanges))
                {
                    OnPropertyChanged(nameof(HasUnsavedChanges));
                }
            };
        }
    }

    /// <summary>The item's dot: its page holds unsaved changes.</summary>
    public bool HasUnsavedChanges => ViewModel?.HasUnsavedChanges ?? false;

    /// <summary>The page's title, in sentence case.</summary>
    public string Title { get; }

    /// <summary>The settings page it opens; null for the Library.</summary>
    public SettingsViewModel? ViewModel { get; }

    /// <summary>The item's icon, a Segoe Fluent Icons glyph.</summary>
    public string Glyph => ViewModel is null ? "" : Title switch
    {
        "General" => "", "Clips" => "", "Hotkeys" => "", "Audio Tracks" => "",
        "Games" => "", "Sounds" => "", _ => "",
    };

    public override string ToString() => Title;
}
