using Encore.Contracts.Settings;

namespace Encore.Core.Library;

/// <summary>When <c>cleanup-limit-unreachable</c> is due: once, until the folder is back within its size limit. Actor thread.</summary>
internal sealed class CleanupLimitWarning
{
    private bool _raised;

    /// <summary>Turning Cleanup off re-arms the warning.</summary>
    public void RearmWhenOff(CleanupSettings limits)
    {
        if (limits.MaxBytes is null && limits.MaxAgeSeconds is null)
        {
            _raised = false;
        }
    }

    /// <summary>A pass ended with nothing left to delete: whether the warning is to be raised now.</summary>
    public bool IsDue(long? maxBytes, CleanupPlan plan)
    {
        // Re-armed only once the folder is back within the size limit, not by a pass that merely skipped a file.
        if (maxBytes is not { } limit || plan.Remaining <= limit)
        {
            _raised = false;
            return false;
        }

        if (!plan.Unreachable || _raised)
        {
            return false;
        }

        _raised = true;
        return true;
    }
}
