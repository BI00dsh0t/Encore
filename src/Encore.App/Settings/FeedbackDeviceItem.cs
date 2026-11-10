namespace Encore.App.Settings;

/// <summary>A device the Feedback Sounds page offers; a null id is the default communications device.</summary>
internal sealed record FeedbackDeviceItem(string? EndpointId, string Label);
