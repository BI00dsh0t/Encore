namespace Encore.Engine.Supervision;

/// <summary>A process the engine watches: the attached App, or one it started.</summary>
internal interface IWatchedProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code once the process has exited.</summary>
    Task<int> Exited { get; }
}
