namespace Encore.App.Settings;

/// <summary>An output resolution the page offers; IsSource is "Source", the game's or screen's own size.</summary>
internal sealed record ResolutionItem(int Width, int Height, string Label, bool IsSource = false);
