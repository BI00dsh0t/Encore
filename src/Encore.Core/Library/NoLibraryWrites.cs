namespace Encore.Core.Library;

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
