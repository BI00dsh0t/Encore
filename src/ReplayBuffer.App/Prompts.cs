using System.Windows;

namespace ReplayBuffer.App;

/// <summary>The App's prompts, in its themed <see cref="PromptDialog"/> (AD-23). Members run on the UI thread.</summary>
internal static class Prompts
{
    /// <summary>
    /// Asks a Yes/No question over <paramref name="view"/>'s window, or with no owner while the view isn't
    /// shown; true is Yes. Yes is the default, and Esc or closing the dialog answers No.
    /// </summary>
    public static bool YesNo(FrameworkElement? view, string message, string caption, MessageBoxImage image) =>
        new PromptDialog(message, caption, image).Ask(view is null ? null : Window.GetWindow(view));
}
