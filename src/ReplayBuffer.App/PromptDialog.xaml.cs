using System.Windows;
using System.Windows.Input;

namespace ReplayBuffer.App;

/// <summary>
/// The themed Yes/No dialog behind <see cref="Prompts"/>, in place of <c>MessageBox</c> (AD-23): an owned,
/// modal <see cref="ShellWindow"/> that permits screenshots/recording (AD-15) and follows the theme live. Yes
/// is the default button and has focus first; Enter activates the focused button. Esc and the close box
/// answer No, as MessageBox's YesNo does with no Cancel. Members run on the UI thread.
/// </summary>
internal partial class PromptDialog
{
    public PromptDialog(string message, string caption, MessageBoxImage image)
    {
        InitializeComponent();
        Title = caption;
        CaptionText.Text = caption;
        MessageText.Text = message;
        (string Text, string Brush)? glyph = image switch
        {
            // Segoe Fluent Icons' Warning, Unknown and ErrorBadge: drawn in the token colour, never as an emoji.
            MessageBoxImage.Warning => ("", "SystemFillColorCautionBrush"),
            MessageBoxImage.Question => ("", "AccentTextFillColorPrimaryBrush"),
            MessageBoxImage.Error => ("", "SystemFillColorCriticalBrush"),
            _ => null,
        };
        if (glyph is { } g)
        {
            Glyph.Text = g.Text;
            Glyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, g.Brush);
            Glyph.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => YesButton.Focus();
    }

    /// <summary>Raised as each dialog is about to show, placed and owned; tests answer through it.</summary>
    internal static event Action<PromptDialog>? Showing;

    /// <summary>The answer: true for Yes, false for No, Esc or the close box.</summary>
    public bool Answer { get; private set; }

    /// <summary>
    /// Shows the dialog modally over <paramref name="owner"/>, centred on it, or centred on the screen and in
    /// the taskbar when there is no shown owner. True is Yes.
    /// </summary>
    public bool Ask(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }

        Showing?.Invoke(this);
        ShowDialog();
        return Answer;
    }

    /// <summary>Answers and closes, as the Yes and No buttons do.</summary>
    internal void Choose(bool yes)
    {
        Answer = yes;
        DialogResult = yes;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Choose(false);
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnYes(object sender, RoutedEventArgs e) => Choose(true);

    private void OnNo(object sender, RoutedEventArgs e) => Choose(false);
}
