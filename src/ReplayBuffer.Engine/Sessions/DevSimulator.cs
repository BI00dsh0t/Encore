using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// Serves <c>dev.simulate</c>: injects a stub Recording state, a disk full or a save failure, so the
/// feedback notices can be exercised before the features behind them exist. Dev and test instances
/// only; the installed instance refuses it.
/// </summary>
/// <param name="recording">Sets the stub Recording state, through the SessionOrchestrator.</param>
/// <param name="diskFull">Reports a full volume.</param>
/// <param name="saveFailed">The engine's not-saved path: publishes <c>clip.failed</c> and its notice.</param>
internal sealed class DevSimulator(Instance instance, Action<bool> recording, Action diskFull, Action<ClipFailed> saveFailed)
{
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
                break;
            case SimulatedEvent.RecordingStopped:
                recording(false);
                break;
            case SimulatedEvent.DiskFull:
                diskFull();
                break;
            case SimulatedEvent.SaveFailed:
                saveFailed(new ClipFailed(ErrorCodes.SaveFailed));
                break;
            default:
                throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"'{simulate.Event}' is not an event dev.simulate injects.");
        }

        return Empty.Value;
    }
}
