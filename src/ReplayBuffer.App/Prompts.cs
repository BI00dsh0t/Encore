using System.Windows;

namespace ReplayBuffer.App;

/// <summary>The App's message-box prompts. Members run on the UI thread.</summary>
internal static class Prompts
{
    /// <summary>
    /// Asks a Yes/No question in a message box over <paramref name="view"/>'s window, or with no owner
    /// while the view isn't shown; true is Yes.
    /// </summary>
    public static bool YesNo(FrameworkElement? view, string message, string caption, MessageBoxImage image)
    {
        var owner = view is null ? null : Window.GetWindow(view);
        var answer = owner is null
            ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, image)
            : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, image);
        return answer == MessageBoxResult.Yes;
    }
}
