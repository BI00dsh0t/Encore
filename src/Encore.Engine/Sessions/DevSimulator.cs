using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.Engine.Sessions;

/// <summary>Serves <c>dev.simulate</c>: fakes engine outcomes so feedback, supervision and recovery can be exercised.</summary>
internal sealed class DevSimulator(
    Instance instance,
    Action<bool> recording,
    Action<RecordingStarted> started,
    Action<RecordingStopped> stopped,
    Action diskFull,
    Action<ClipFailed> saveFailed,
    Action saved,
    Action hang,
    Action gpuReset)
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
