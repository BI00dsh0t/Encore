using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Library;
using Encore.Core.Output;

namespace Encore.Core.Sessions;

/// <summary>How the orchestrator turns a Record press into a Recording; every callback runs on the orchestrator thread.</summary>
public sealed record Recordings(
    OutputPathAllocator Paths,
    Func<string> DefaultOutputFolder,
    Action<RecordingStarted> Started,
    Action<RecordingStopped> Stopped,
    Action<Exception> Failed,
    ILibraryWrites? Library = null,
    Action<string>? Info = null,
    IOpenRecordings? Open = null,
    Action<RecordingRestart>? Restarted = null,
    Action? DiskFull = null,
    Func<EffectiveSettings, bool>? RingFits = null,
    Action<bool>? ReplayDiskFull = null,
    TimeSpan? RingRecheck = null,
    Action<RecordingStarted>? MonitorStarted = null,
    Action<RecordingStopped>? MonitorStopped = null,
    Action<string, string>? Warn = null)
{
    /// <summary>How often a Rewind a full disk stopped checks for room for its ring by default.</summary>
    public static readonly TimeSpan DefaultRingRecheck = TimeSpan.FromSeconds(30);
}
