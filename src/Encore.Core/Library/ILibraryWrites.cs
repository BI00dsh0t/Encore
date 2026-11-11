namespace Encore.Core.Library;

/// <summary>The "file being written" signal: a writer tells the Library which file it writes and how that ended.</summary>
public interface ILibraryWrites
{
    /// <summary>A write is starting at this final path for a Game Profile; called before the writer creates any data.</summary>
    void BeginWrite(string path, string? profileId);

    /// <summary>The same, with the file's kind, which the Library keeps with the item; by default the kind is dropped.</summary>
    void BeginWrite(string path, string? profileId, string? kind) => BeginWrite(path, profileId);

    /// <summary>The write has ended: after the rename or final flush when written, or after the cleanup when failed.</summary>
    void EndWrite(string path, WriteOutcome outcome);
}
