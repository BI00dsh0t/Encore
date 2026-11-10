using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Monitors;

/// <summary>The screens a Monitor Recording will use, and the chosen ones that aren't attached (by the id the setting holds).</summary>
public sealed record ResolvedScreens(IReadOnlyList<DisplayMonitor> Screens, IReadOnlyList<string> Missing);

/// <summary>Core's one resolver of a Monitor Recording's screens: the screens key when set, else the older single-monitor key.</summary>
public static class MonitorScreens
{
    /// <summary>The attached screens to record, the primary first; ids and names match ignoring case. docs/code-notes.md.</summary>
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
