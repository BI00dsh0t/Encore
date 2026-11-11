namespace Encore.Platform.Windows.Stores;

/// <summary>Reads one store's game folders. It may throw; a file it can't read it names in unreadable and skips.</summary>
public interface IStoreReader
{
    GameStore Store { get; }

    /// <summary>The folders games are installed under, as the store records them (not yet canonical).</summary>
    IReadOnlyList<string> Folders(ICollection<string> unreadable);
}
