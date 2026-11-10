namespace Encore.Core.Library;

/// <summary>How a write the Library was told about ended.</summary>
public enum WriteOutcome
{
    /// <summary>The file is complete at its final path.</summary>
    Written,

    /// <summary>The write failed and its files were removed.</summary>
    Failed,
}

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

/// <summary>The no-op port for a host or test with no Library (<c>SessionOrchestrator</c>'s default): it ignores every call.</summary>
public sealed class NoLibraryWrites : ILibraryWrites
{
    public static NoLibraryWrites Instance { get; } = new();

    private NoLibraryWrites()
    {
    }

    /// <inheritdoc/>
    public void BeginWrite(string path, string? profileId)
    {
    }

    /// <inheritdoc/>
    public void EndWrite(string path, WriteOutcome outcome)
    {
    }
}
