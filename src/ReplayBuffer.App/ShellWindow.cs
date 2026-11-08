using System.Windows;
using ReplayBuffer.App.Theme;

namespace ReplayBuffer.App;

/// <summary>
/// The base of the App's own windows. Windows remain available to screenshots and recording (AD-15);
/// their frame follows the theme as soon as the native handle is initialized (AD-23).
/// </summary>
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
