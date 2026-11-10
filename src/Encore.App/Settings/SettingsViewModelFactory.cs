using Encore.App.Engine;

namespace Encore.App.Settings;

/// <summary>The pages of the Settings area, in navigation order.</summary>
internal static class SettingsViewModelFactory
{
    /// <summary>Every page in nav order: General, Clips, Hotkeys, Audio Tracks, Games, Sounds, then Storage and Cleanup.</summary>
    public static IReadOnlyList<SettingsViewModel> Create(
        SettingsSender sender,
        IEngineSession engine,
        string? defaultCacheFolder = null,
        IGameShell? gameShell = null) =>
    [
        new GeneralSettingsViewModel(sender),
        new RewindSettingsViewModel(sender, defaultCacheFolder: defaultCacheFolder),
        new HotkeysViewModel(sender, engine),
        new TrackLayoutViewModel(sender, engine),
        new GameProfilesViewModel(sender, engine, gameShell),
        new FeedbackSoundsViewModel(sender, engine),
        new LibrarySettingsViewModel(sender, engine, defaultCacheFolder),
    ];
}
