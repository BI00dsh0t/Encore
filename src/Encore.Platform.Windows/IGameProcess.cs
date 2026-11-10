namespace Encore.Platform.Windows;

/// <summary>A game's process, opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c> only.</summary>
public interface IGameProcess : IDisposable
{
    /// <summary>The process's executable, as the system reports it (not canonical).</summary>
    string ImagePath { get; }

    bool HasExited { get; }

    /// <summary>When the process started, UTC; <see cref="DateTime.MinValue"/> when it couldn't be read.</summary>
    DateTime StartTime { get; }
}
