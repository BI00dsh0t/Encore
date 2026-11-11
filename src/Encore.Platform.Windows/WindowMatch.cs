namespace Encore.Platform.Windows;

/// <summary>The result of matching a title: the window when exactly one matched, otherwise the candidates to list.</summary>
public sealed record WindowMatch(WindowMatchOutcome Outcome, WindowInfo? Window, IReadOnlyList<WindowInfo> Candidates);
