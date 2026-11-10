using System.Windows;
using Encore.App.Theme;

namespace Encore.App;

/// <summary>The base of the App's own windows: screenshots stay allowed, and the frame follows the theme.</summary>
internal class ShellWindow : Window
{
    /// <summary>Whether Fluent shows Mica behind this window; only the Main App's does (DESIGN.md).</summary>
    internal virtual bool UsesMica => false;

    protected override void OnSourceInitialized(EventArgs e)
    {
        ThemeService.Current?.Decorate(this);
        base.OnSourceInitialized(e);
    }
}
