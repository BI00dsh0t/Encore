using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Engine.Settings;

/// <summary>
/// The first-run seed (R6, AD-24): when <c>settings.json</c> has no Global <c>trackLayout</c>, the
/// engine writes the Simple preset from the Windows default devices, once, through the store's own
/// update path (AD-4). An existing value, Global or a profile's, is never touched, and the catalog
/// default is unchanged.
/// </summary>
internal static class TrackLayoutSeed
{
    /// <summary>Seeds <paramref name="store"/> when its document has no Global <c>trackLayout</c>.</summary>
    /// <returns>The seeded layout, or <c>null</c> when the document has one already or the write failed (logged).</returns>
    public static TrackLayout? Apply(SettingsStore store, IReadOnlyList<AudioDevice> devices, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(devices);
        var current = store.Current;
        if (current.Global.ContainsKey(Keys.TrackLayout.Name))
        {
            return null;
        }

        var layout = TrackLayoutPresets.Seed(devices);
        try
        {
            store.Update(new SettingsUpdate(current.Revision, [SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout))]));
        }
        catch (ErrorCodeException e)
        {
            log.LogWarning("Couldn't write the first-run Track Layout: {Message}", e.Message);
            return null;
        }

        log.LogInformation(
            "First run: seeded the Track Layout with {Devices}",
            layout.Devices.Count == 0 ? "no devices" : string.Join(", ", layout.Devices.Select(d => $"{d.FriendlyName} ({d.Flow})")));
        return layout;
    }
}
