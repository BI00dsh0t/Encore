namespace ReplayBuffer.Core.Library;

/// <summary>How a write the Library was told about ended.</summary>
public enum WriteOutcome
{
    /// <summary>The file is complete at its final path.</summary>
    Written,

    /// <summary>The write failed and its files were removed.</summary>
    Failed,
}

/// <summary>
/// The "file being written" signal (R6): a writer tells the Library which file it is about to write,
/// and how that ended, so the Library can show it as in progress and ignore its partial data.
/// Clips use it now; epic-recordings reuses the port, and epic-library-cleanup's LibraryActor
/// replaces <see cref="NoLibraryWrites"/>. Neither call blocks or throws, and any thread may call
/// them (the LibraryActor posts to its own queue).
/// </summary>
public interface ILibraryWrites
{
    /// <summary>
    /// A write is starting at <paramref name="path"/>, the absolute final path, for the Game Profile
    /// <paramref name="profileId"/> (null for none). Called before the writer creates its temp or
    /// final data; the reserved empty file may already exist.
    /// </summary>
    void BeginWrite(string path, string? profileId);

    /// <summary>
    /// The write at <paramref name="path"/> has ended: after the rename or final flush for
    /// <see cref="WriteOutcome.Written"/>, or after the failure's cleanup for <see cref="WriteOutcome.Failed"/>.
    /// </summary>
    void EndWrite(string path, WriteOutcome outcome);
}

/// <summary>The stand-in until the LibraryActor exists: it ignores every call.</summary>
public sealed class NoLibraryWrites : ILibraryWrites
{
    /// <summary>The one instance.</summary>
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
