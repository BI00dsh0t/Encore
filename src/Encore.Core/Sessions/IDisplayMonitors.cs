using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

/// <summary>The attached monitors and their changes, so the orchestrator can end a Monitor Recording whose monitor is gone.</summary>
public interface IDisplayMonitors
{
    /// <summary>Every attached monitor now, the primary first. Never throws; empty when they can't be read.</summary>
    IReadOnlyList<DisplayMonitor> List();

    /// <summary>Calls back after each display change, on any thread, until the subscription is disposed. Never throws.</summary>
    IDisposable Subscribe(Action changed);
}
