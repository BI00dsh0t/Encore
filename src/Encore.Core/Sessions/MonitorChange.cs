using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

/// <summary>How the attached monitors differ from the ones a Monitor Recording composites, by GDI device name.</summary>
internal sealed record MonitorChange(IReadOnlyList<string> Arrived, IReadOnlyList<string> Gone)
{
    public static MonitorChange Compare(IReadOnlyList<string> recorded, IReadOnlyList<DisplayMonitor> attached)
    {
        var arrived = attached
            .Where(monitor => monitor.Width > 0 && !recorded.Any(device => Same(device, monitor.DeviceName)))
            .Select(monitor => monitor.DeviceName)
            .ToList();
        var gone = recorded.Where(device => !attached.Any(monitor => Same(monitor.DeviceName, device))).ToList();
        return new MonitorChange(arrived, gone);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
