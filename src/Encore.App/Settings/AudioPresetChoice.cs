using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A preset in the Use a preset menu.</summary>
internal sealed record AudioPresetChoice(TrackLayoutPreset Preset, string Name, string Label);
