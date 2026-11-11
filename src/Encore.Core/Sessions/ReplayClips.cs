using Encore.Contracts.Messages;
using Encore.Core.Library;
using Encore.Core.Output;

namespace Encore.Core.Sessions;

/// <summary>How the orchestrator turns a save press into a Clip; every callback runs on the orchestrator thread.</summary>
public sealed record ReplayClips(
    OutputPathAllocator Paths,
    Func<string> DefaultOutputFolder,
    Action<ClipSaved> Saved,
    Action<Exception> Failed,
    Action<ClipFailed>? NotSaved = null,
    TimeSpan? ExitDrain = null,
    Action<ClipSaving>? Saving = null,
    Action<TimeSpan>? Ignored = null,
    ILibraryWrites? Library = null)
{
    /// <summary>At most this many Clips are written at once, across sessions.</summary>
    public const int MaxWriters = 2;

    /// <summary>The exit drain: 30 s.</summary>
    public static readonly TimeSpan DefaultExitDrain = TimeSpan.FromSeconds(30);
}
