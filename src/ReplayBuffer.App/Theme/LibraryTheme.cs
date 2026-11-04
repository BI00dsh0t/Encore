using System.Windows;

namespace ReplayBuffer.App.Theme;

/// <summary>
/// The Library's dictionary (story 4), one for both themes: its styles take every value from the token
/// keys, so they follow whichever theme is merged. <see cref="ThemeService"/> places it on each apply.
/// </summary>
internal partial class LibraryTheme
{
    public LibraryTheme() => InitializeComponent();

    /// <summary>
    /// Adds one <see cref="LibraryTheme"/> to <paramref name="merged"/> unless it has one. Its keys are its
    /// own, so where it sits doesn't matter; it goes before the last so the theme's dictionary stays last.
    /// </summary>
    public static void Place(IList<ResourceDictionary> merged)
    {
        if (merged.Any(d => d is LibraryTheme))
        {
            return;
        }

        merged.Insert(Math.Max(0, merged.Count - 1), new LibraryTheme());
    }
}
