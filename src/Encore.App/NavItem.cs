using Encore.App.Settings;

namespace Encore.App;

/// <summary>One item of the shell's navigation: the Library, the Settings section label, or a settings page.</summary>
internal sealed class NavItem : Observable
{
    public NavItem(string title, SettingsPage? page, bool isSection)
    {
        Title = title;
        Page = page;
        IsSection = isSection;
        if (page is not null)
        {
            page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsPage.HasUnsavedChanges))
                {
                    OnPropertyChanged(nameof(HasUnsavedChanges));
                }
            };
        }
    }

    /// <summary>The nav item's dot: its page holds unsaved changes.</summary>
    public bool HasUnsavedChanges => Page?.HasUnsavedChanges ?? false;

    /// <summary>The page's title, in sentence case.</summary>
    public string Title { get; }

    /// <summary>The settings page it opens; null for the Library and the section label.</summary>
    public SettingsPage? Page { get; }

    /// <summary>The non-selectable Settings label above the settings pages.</summary>
    public bool IsSection { get; }

    /// <summary>The item's icon, a Segoe Fluent Icons glyph.</summary>
    public string Glyph => Page is null ? "\uE8F1" : Title switch
    {
        "General" => "\uE713", "Rewind" => "\uE714", "Hotkeys" => "\uE765",
        "Audio Tracks" => "\uE8D6", "Games" => "\uE7FC", "Feedback Sounds" => "\uE767", _ => "\uE8B7",
    };

    public override string ToString() => Title;
}
