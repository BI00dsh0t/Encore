namespace ReplayBuffer.App.Theme;

/// <summary>
/// Games' dictionary (story 10.10), one for both themes: its styles take every value from the token keys,
/// so they follow whichever theme is merged. <see cref="ThemeService"/> places it on each apply.
/// </summary>
internal partial class GamesTheme
{
    public GamesTheme() => InitializeComponent();
}
