namespace Encore.Platform.Windows.Stores;

/// <summary>A store's game folder: canonical, or lowercase when it isn't on disk, with a trailing <c>\</c>.</summary>
public sealed record StoreFolder(string Path, GameStore Store);
