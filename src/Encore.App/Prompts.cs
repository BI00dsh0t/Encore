using System.Windows;

namespace Encore.App;

/// <summary>The App's prompts, in its themed <see cref="PromptDialog"/>. Members run on the UI thread.</summary>
internal static class Prompts
{
    /// <summary>Asks a Yes/No question over a view's window; true is Yes, and Esc or closing the dialog answers No.</summary>
    public static bool Ask(FrameworkElement? view, string message, string caption) =>
        YesNo(view, message, caption, MessageBoxImage.Question);

    /// <summary>As <see cref="Ask"/>, with the warning icon: for a Yes that deletes files.</summary>
    public static bool Warn(FrameworkElement? view, string message, string caption) =>
        YesNo(view, message, caption, MessageBoxImage.Warning);

    private static bool YesNo(FrameworkElement? view, string message, string caption, MessageBoxImage image) =>
        new PromptDialog(message, caption, image).Ask(view is null ? null : Window.GetWindow(view));
}
