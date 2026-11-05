using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Engine.Ipc;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// Turns each <see cref="MemoryMargin"/> event into one <c>memory-margin</c> warning (R3): severity
/// warning, source <c>replay</c>, no overlay, dismissable. Leaving the margin raises and dismisses
/// nothing: warnings go only by <c>warning.dismiss</c> (AD-3).
/// </summary>
internal sealed class MemoryMarginReporter : IDisposable
{
    private readonly MemoryMargin _margin;
    private readonly Warnings _warnings;
    private int _disposed;

    public MemoryMarginReporter(MemoryMargin margin, Warnings warnings)
    {
        _margin = margin;
        _warnings = warnings;
        _margin.Raised += OnRaised;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _margin.Raised -= OnRaised;
        }
    }

    private void OnRaised(MemoryMarginEvent raised) =>
        _warnings.Raise(
            ErrorCodes.MemoryMargin,
            WarningSeverity.Warning,
            "replay",
            raised == MemoryMarginEvent.ShortRing
                ? "Memory is so low that ReplayBuffer keeps less than 1 s of gameplay. Saving makes no Clip until memory frees up."
                : "Memory is low, so ReplayBuffer stopped keeping more gameplay. Clips may be shorter than your Clip length.",
            overlay: false);
}
