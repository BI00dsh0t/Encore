namespace Encore.Engine.Settings;

/// <summary>What <see cref="SettingsStore.Open"/> found: the store, and an error code if the file was unreadable.</summary>
public sealed record SettingsLoad(SettingsStore Store, string? LoadCode);
