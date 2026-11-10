using System.Windows;

namespace Encore.App;

/// <summary>The App's prompts, in its themed <see cref="PromptDialog"/>. Members run on the UI thread.</summary>
internal static class Prompts
{
    /// <summary>Asks a Yes/No question over a view's window; true is Yes, and Esc or closing the dialog answers No.</summary>
    public static bool YesNo(FrameworkElement? view, string message, string caption, MessageBoxImage image) =>
        new PromptDialog(message, caption, image).Ask(view is null ? null : Window.GetWindow(view));
}
