using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Core.Monitors;

/// <summary>The screens a Monitor Recording will use, and the chosen ones that aren't attached (by the id the setting holds).</summary>
public sealed record ResolvedScreens(IReadOnlyList<DisplayMonitor> Screens, IReadOnlyList<string> Missing);

/// <summary>
/// Core's one resolver of a Monitor Recording's screens (AD-25, story 11.2): <c>monitorRecordingScreens</c>
/// when it is set, else the old <c>monitorRecordingMonitor</c> exactly as before.
/// </summary>
public static class MonitorScreens
{
    /// <summary>
    /// The attached screens to record, in <paramref name="attached"/>'s order (the primary first, as
    /// <c>monitors.list</c> gives them).
    /// <list type="bullet">
    /// <item><paramref name="screens"/> All: every attached monitor.</item>
    /// <item>Selected: each id matched to an attached monitor by <see cref="DisplayMonitor.MonitorId"/>,
    /// else by <see cref="DisplayMonitor.DeviceName"/> (a value carried over from the old key); the
    /// unmatched ids are <see cref="ResolvedScreens.Missing"/>. None attached gives no screens.</item>
    /// <item>Null: <paramref name="legacyDeviceName"/>'s monitor, else (null, or no monitor has that name)
    /// the primary one, as <c>monitorRecordingMonitor</c> always resolved.</item>
    /// </list>
    /// Ids and device names match ignoring case.
    /// </summary>
    public static ResolvedScreens Resolve(MonitorRecordingScreens? screens, string? legacyDeviceName, IReadOnlyList<DisplayMonitor> attached)
    {
        if (screens is null)
        {
            var legacy = (legacyDeviceName is null ? null : attached.FirstOrDefault(m => Same(m.DeviceName, legacyDeviceName)))
                ?? attached.FirstOrDefault(m => m.Primary)
                ?? attached.FirstOrDefault();
            return new ResolvedScreens(legacy is null ? [] : [legacy], []);
        }

        if (screens.Mode == MonitorScreensMode.All)
        {
            return new ResolvedScreens(attached, []);
        }

        var chosen = new HashSet<DisplayMonitor>(ReferenceEqualityComparer.Instance);
        var missing = new List<string>();
        foreach (var id in screens.Monitors ?? [])
        {
            var match = attached.FirstOrDefault(m => m.MonitorId is { } monitorId && Same(monitorId, id))
                ?? attached.FirstOrDefault(m => Same(m.DeviceName, id));
            if (match is null)
            {
                missing.Add(id);
            }
            else
            {
                chosen.Add(match);
            }
        }

        return new ResolvedScreens([.. attached.Where(chosen.Contains)], missing);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
