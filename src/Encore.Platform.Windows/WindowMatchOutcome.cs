namespace Encore.Platform.Windows;

/// <summary>How a window title matched the open windows.</summary>
public enum WindowMatchOutcome
{
    Found,
    NotFound,
    Ambiguous,
}
