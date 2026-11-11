using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Core.Settings;
using Encore.Engine.Ipc;
using Encore.Engine.Settings;
using Encore.Platform.Windows;
using Encore.Platform.Windows.Stores;

namespace Encore.Engine.Hosting;

/// <summary>Builds Game Detection's monitor: which programs are games, which are left out, and where a find goes.</summary>
internal static class GameDetectionSetup
{
    public static GameMonitor Build(
        EngineHostOptions options,
        SettingsStore store,
        EventHub hub,
        Warnings warnings,
        SessionOrchestrator sessions,
        ILogger log)
    {
        var listed = ListedGames(new GameConfigStore(log), () => store.Current);
        var stores = options.GameStores ?? new StoreLibraries(log).Folders;
        return new GameMonitor(
            options.GameWindows ?? new ForegroundGameWindowProbe(),
            pid => pid == Environment.ProcessId || pid == hub.State.AppPid,
            (to, reason) => sessions.Post(new ProposeTarget(to, reason)),
            options.GameScanInterval,
            log,
            listed,
            InstallFolder(),
            game => Detected(WithManualSource(game, store.Current), store, hub, warnings, log),
            game => hub.Publish(GameMessages.Exited, game),
            () => hub.State.TargetGame?.ProcessId,
            options.Time,
            id => store.Current.Profiles.TryGetValue(id, out var profile) && profile.Ignored,
            path => ProfileMatch.Resolve(store.Current, path),
            stores);
    }

    private static void Detected(DetectedGame game, SettingsStore store, EventHub hub, Warnings warnings, ILogger log)
    {
        // The profile exists before anything names it; the notice comes once per game, as its profile is created.
        GameProfiles.EnsureProfile(
            store,
            game.ProfileId,
            game.Name,
            game.Source,
            log,
            name => warnings.Raise(
                ErrorCodes.NewGameNotCaptured,
                WarningSeverity.Info,
                "games",
                NotCapturedText(name, game.Source),
                overlay: true));
        hub.Publish(GameMessages.Detected, game);
    }

    /// <summary>The one-time notice for a new game that isn't captured; an app found in fullscreen is told apart.</summary>
    internal static string NotCapturedText(string name, string? source) => source == GameSources.Fullscreen
        ? $"Encore found {name} in fullscreen. If it's a game, turn it on in Games."
        : $"{name} was found but isn't captured. Turn it on in Games.";

    /// <summary>A game added by hand is reported as such, whatever else listed it.</summary>
    private static DetectedGame WithManualSource(DetectedGame game, SettingsDocument document) =>
        GameProfiles.IsManual(document, game.ProfileId) ? game with { Source = GameSources.Manual } : game;

    /// <summary>The listed games for Game Detection, rebuilt only when a source changes. Called on the monitor's thread.</summary>
    private static Func<IReadOnlySet<string>> ListedGames(GameConfigStore windowsList, Func<SettingsDocument> settings)
    {
        IReadOnlySet<string>? fromList = null;
        SettingsDocument? fromSettings = null;
        IReadOnlySet<string> listed = new HashSet<string>(StringComparer.Ordinal);
        return () =>
        {
            var list = windowsList.Paths();
            var document = settings();
            if (!ReferenceEquals(list, fromList) || !ReferenceEquals(document, fromSettings))
            {
                var union = new HashSet<string>(list, StringComparer.Ordinal);
                union.UnionWith(SettingsResolver.Resolve(document, null).ManualGames.Select(GamePaths.CanonicalOrLowercase));
                (fromList, fromSettings, listed) = (list, document, union);
            }

            return listed;
        };
    }

    /// <summary>The engine's install folder, canonical with a trailing <c>\</c>; nothing under it is a game.</summary>
    private static string InstallFolder()
    {
        string folder;
        try
        {
            folder = GamePaths.Canonicalize(AppContext.BaseDirectory);
        }
        catch (Exception e) when (GamePaths.CanonicalizeFailed(e))
        {
            folder = Path.GetFullPath(AppContext.BaseDirectory).ToLowerInvariant();
        }

        return folder.EndsWith('\\') ? folder : folder + '\\';
    }
}
