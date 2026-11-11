namespace Encore.Core.Sessions;

/// <summary>The PC's sleep and resume; the orchestrator decides what a sleep stops and what a resume rebuilds.</summary>
public interface IPowerNotifications
{
    /// <summary>Calls back on each power change, on any thread, until the subscription is disposed. Never throws.</summary>
    IDisposable Subscribe(Action<PowerChange> changed);
}
