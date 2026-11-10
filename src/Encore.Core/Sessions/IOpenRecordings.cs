namespace Encore.Core.Sessions;

/// <summary>Where the orchestrator keeps the paths of Recordings being written, so a restart can trim those left open.</summary>
public interface IOpenRecordings
{
    /// <summary>A Recording at <paramref name="path"/> is about to be written.</summary>
    void Opened(string path);

    /// <summary>The Recording at <paramref name="path"/> is no longer being written, however it ended.</summary>
    void Closed(string path);
}
