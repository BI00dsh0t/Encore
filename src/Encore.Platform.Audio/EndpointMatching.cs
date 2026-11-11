using Encore.Contracts.Messages;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Platform.Audio;

/// <summary>How an open device is found among the present endpoints: by its id, or by its two names.</summary>
internal static class EndpointMatching
{
    public static AudioFlow ToPlatform(SettingsFlow flow) => flow == SettingsFlow.Output ? AudioFlow.Output : AudioFlow.Input;

    public static SettingsFlow ToSettings(AudioFlow flow) => flow == AudioFlow.Output ? SettingsFlow.Output : SettingsFlow.Input;

    /// <summary>The present endpoint of this flow with this id, ignoring case; null when there is none.</summary>
    public static AudioDevice? ById(IReadOnlyList<AudioDevice> present, SettingsFlow flow, string id) =>
        present.FirstOrDefault(device => device.Flow == flow && string.Equals(device.EndpointId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the endpoint is present under its own id.</summary>
    public static bool IsListed(IReadOnlyList<AudioDevice> present, AudioEndpoint endpoint) =>
        ById(present, ToSettings(endpoint.Flow), endpoint.Id) is not null;

    /// <summary>Whether a present endpoint has this flow and exactly these names.</summary>
    public static bool HasNames(AudioDevice device, SettingsFlow flow, string friendlyName, string parentName) =>
        device.Flow == flow
        && string.Equals(device.FriendlyName, friendlyName, StringComparison.Ordinal)
        && string.Equals(device.ParentName, parentName, StringComparison.Ordinal);

    /// <summary>Whether two endpoints have the same flow and names, so a return can't tell which is which.</summary>
    public static bool SameName(AudioEndpoint first, AudioEndpoint second) =>
        first.Flow == second.Flow
        && string.Equals(first.FriendlyName, second.FriendlyName, StringComparison.Ordinal)
        && string.Equals(first.ParentName, second.ParentName, StringComparison.Ordinal);
}
