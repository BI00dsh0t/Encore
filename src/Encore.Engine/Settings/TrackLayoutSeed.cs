using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Engine.Settings;

/// <summary>The first-run seed: with no Global <c>trackLayout</c>, writes the Simple preset from the default devices, once.</summary>
internal static class TrackLayoutSeed
{
    /// <summary>Seeds the store; null when it already has a layout, no default device is listed, or the write failed.</summary>
    public static TrackLayout? Apply(SettingsStore store, IReadOnlyList<AudioDevice> devices, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(devices);
        var current = store.Current;
        if (current.Global.ContainsKey(Keys.TrackLayout.Name))
        {
            return null;
        }

        // With no default device the seed would equal the catalog default, so nothing is written and a later start retries.
        var layout = TrackLayoutPresets.Seed(devices);
        if (layout.Devices.Count == 0)
        {
            log.LogInformation("First run: no default audio device to seed the Track Layout with; trying again at the next start");
            return null;
        }

        try
        {
            var op = SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout));
            store.Update(new SettingsUpdate(current.Revision, [op]));
        }
        catch (ErrorCodeException e)
        {
            log.LogWarning("Couldn't write the first-run Track Layout: {Message}", e.Message);
            return null;
        }

        log.LogInformation(
            "First run: seeded the Track Layout with {Devices}",
            layout.Devices.Count == 0
                ? "no devices"
                : string.Join(", ", layout.Devices.Select(device => $"{device.FriendlyName} ({device.Flow})")));
        return layout;
    }
}
