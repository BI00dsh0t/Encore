using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.Engine.Sessions;

/// <summary>
/// Serves <c>dev.simulate</c>: injects a stub Recording (its state, and its started and stopped
/// notices with reason <c>stopped</c>), a disk full, a save failure or a saved notice, so the
/// feedback notices can be exercised before the features behind them exist, an engine hang, so
/// the App's supervision can be, or a GPU driver reset, so the sessions' recovery can be. Dev and
/// test instances only; the installed instance refuses it.
/// </summary>
/// <param name="recording">Sets the stub Recording state, through the SessionOrchestrator; it drives the solid dot only.</param>
/// <param name="started">Raises the Recording started notice, as a real <c>recording.started</c> would.</param>
/// <param name="stopped">Raises the Recording stopped notice, as a real <c>recording.stopped</c> would.</param>
/// <param name="diskFull">Reports a full volume.</param>
/// <param name="saveFailed">The engine's not-saved path: publishes <c>clip.failed</c> and its notice.</param>
/// <param name="saved">Raises the saved notice only; no <c>clip.saved</c> is published, as no file exists.</param>
/// <param name="hang">Stops the heartbeat and freezes the event hub, so the engine looks hung while its process lives.</param>
/// <param name="gpuReset">Marks every live GPU device lost, as a driver reset would, so the sessions on them recover (R3).</param>
internal sealed class DevSimulator(Instance instance, Action<bool> recording, Action<RecordingStarted> started, Action<RecordingStopped> stopped, Action diskFull, Action<ClipFailed> saveFailed, Action saved, Action hang, Action gpuReset)
{
    private const string SimulatedPath = "dev.simulate";

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.NotDevInstance"/> in the installed instance.</exception>
    public Empty Simulate(DevSimulate simulate)
    {
        ArgumentNullException.ThrowIfNull(simulate);
        if (instance.Name == Instance.InstalledName)
        {
            throw new ErrorCodeException(ErrorCodes.NotDevInstance, "dev.simulate is refused by the installed instance.");
        }

        switch (simulate.Event)
        {
            case SimulatedEvent.RecordingStarted:
                recording(true);
                started(new RecordingStarted(SimulatedPath));
                break;
            case SimulatedEvent.RecordingStopped:
                recording(false);
                stopped(new RecordingStopped(SimulatedPath, RecordingStopReasons.Stopped));
                break;
            case SimulatedEvent.DiskFull:
                diskFull();
                break;
            case SimulatedEvent.SaveFailed:
                saveFailed(new ClipFailed(ErrorCodes.SaveFailed));
                break;
            case SimulatedEvent.ClipSaved:
                saved();
                break;
            case SimulatedEvent.EngineHang:
                hang();
                break;
            case SimulatedEvent.GpuReset:
                gpuReset();
                break;
            default:
                throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"'{simulate.Event}' is not an event dev.simulate injects.");
        }

        return Empty.Value;
    }
}
