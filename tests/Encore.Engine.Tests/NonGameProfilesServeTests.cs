using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Platform.Windows;

namespace Encore.Engine.Tests;

/// <summary>The one-time check at start that stops capturing profiles of programs that are never games.</summary>
public sealed class NonGameProfilesServeTests : IDisposable
{
    private const string Chrome = @"c:\program files\google\chrome\application\chrome.exe";
    private const string Codex = @"c:\program files\windowsapps\openai.codex_26.1103.4239.0_x64__2p2nqsd0c76g0\app\chatgpt.exe";
    private const string Game = @"d:\steamlibrary\steamapps\common\game\game.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-nongames-" + Guid.NewGuid().ToString("N"));
    private readonly Instance _instance;

    public NonGameProfilesServeTests()
    {
        _instance = new Instance(
            Instance.TestPrefix + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string LockScreen =>
        Path.Combine(NeverGames.WindowsFolder, @"systemapps\microsoft.lockapp_cw5n1h2txyewy\lockapp.exe");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task TheCheckStopsCapturingNeverGamesOnceAndLeavesOtherAppsAlone()
    {
        WriteSettings(new GameProfile { Name = "Google Chrome" }, new GameProfile { Name = "Lock screen" });

        var first = await StateAsync();
        Assert.True(first.Settings.Profiles[Chrome].Ignored);
        Assert.True(first.Settings.Profiles[LockScreen].Ignored);
        Assert.False(first.Settings.Profiles[Codex].Ignored);
        Assert.False(first.Settings.Profiles[Game].Ignored);
        var notice = Assert.Single(first.Warnings, w => w.Code == ErrorCodes.NewGameNotCaptured);
        Assert.Equal(
            "Encore stopped capturing Google Chrome and Lock screen; they aren't games. Turn them back on in Games.",
            notice.Message);

        // Turned back on by the user: the check never runs again.
        WriteSettings(new GameProfile { Name = "Google Chrome" }, new GameProfile { Name = "Lock screen", Ignored = true });
        var second = await StateAsync();
        Assert.False(second.Settings.Profiles[Chrome].Ignored);
        Assert.DoesNotContain(second.Warnings, w => w.Code == ErrorCodes.NewGameNotCaptured);
    }

    private void WriteSettings(GameProfile chrome, GameProfile lockScreen)
    {
        var profiles = SettingsMaps.EmptyProfiles
            .Add(Chrome, chrome)
            .Add(LockScreen, lockScreen)
            .Add(Codex, new GameProfile { Name = "Codex", Source = GameSources.Fullscreen })
            .Add(Game, new GameProfile { Name = "Game", Source = GameSources.Steam });
        Directory.CreateDirectory(_instance.RoamingDataFolder);
        var document = SettingsDocument.Empty with { Revision = 1, Profiles = profiles };
        File.WriteAllText(
            Path.Combine(_instance.RoamingDataFolder, "settings.json"),
            JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument));
    }

    // Starts the engine on the instance, reads its first state and stops it again.
    private async Task<StateSnapshot> StateAsync()
    {
        await using var host = EngineHost.Start(
            _instance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions
            {
                Heartbeat = null,
                ProbeEncoders = false,
                AppLauncher = new GameDetectionServeTests.StayingLauncher(),
            });
        await using var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
        return client.State!;
    }
}
