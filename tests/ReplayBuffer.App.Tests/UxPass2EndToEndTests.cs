using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Engine;
using ReplayBuffer.Engine.Supervision;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// Story 10.8's closing check of epic 10, unattended and with no capture: the whole engine
/// (<c>EngineHost</c>) in-process on a throwaway test instance in temp folders, with fake audio
/// endpoints and a Recycle Bin that deletes the temp files, and the App's own
/// <see cref="EngineConnection"/> and view models. It starts from an empty settings folder and checks the
/// seed; drives a fabricated Library through selection, a bulk Favourite, Copy, sort, search and a
/// delete that keeps the Favourites; applies and saves a preset; changes and resets a game's Clip length;
/// and checks the nav has no Recordings page. The glossary and every other App test run in the same suite.
/// </summary>
[Collection(nameof(WpfCollection))]
public sealed class UxPass2EndToEndTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly AudioDevice Speakers = new("{0.0.0.00000000}.{speakers}", "Speakers", "Realtek", AudioFlow.Output, true, IsDefault: true);
    private static readonly AudioDevice Headset = new("{0.0.0.00000000}.{headset}", "Headset", "Arctis", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{0.0.1.00000000}.{mic}", "Microphone", "BEACN Mic", AudioFlow.Input, true, IsDefault: true);
    private const string GameId = @"c:\games\demo\demo.exe";

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-e2e-ux2-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine's log may be released a moment late; the temp folder is left behind.
        }
    }

    [Fact]
    public void EpicTensDoneWhenHoldsAgainstTheRealEngine() => Sta.Run(() =>
    {
        // The temp folder's name has a dot, which isn't a test instance name.
        var instance = new Instance(Instance.TestPrefix + "ux2-" + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        Assert.True(instance.IsTest);
        Assert.False(Directory.Exists(instance.RoamingDataFolder));
        var output = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Action<Action> post = action => dispatcher.BeginInvoke(action);

        using var session = Session.Start(instance, _folder);
        var engine = session.Engine;
        var main = new MainViewModel(engine, post);
        engine.Changed += () => post(() => main.Refresh());
        main.Refresh();

        // R6: an empty settings folder is seeded with the default playback and microphone on Track 1.
        var seeded = Layout(engine.State!.Settings);
        Assert.Equal([(Speakers.EndpointId, false), (Mic.EndpointId, true)], seeded.Devices.Select(d => (d.EndpointId, d.IsMic)));
        Assert.Equal(Keys.EverythingTrackId, Assert.Single(seeded.Tracks).Id);
        Assert.Equal(["Everything"], main.TrackLayout.Tracks.Select(t => t.Name));

        // R10: the settings pages are named for what they control, and there is no Recordings page.
        Assert.DoesNotContain(main.NavItems, i => i.Title.Contains("Recording", StringComparison.OrdinalIgnoreCase));

        // R5: a preset applied and saved.
        main.TrackLayout.Confirm = (_, _) => true;
        main.TrackLayout.ApplyPresetCommand.Execute(TrackLayoutPreset.ForEditing);
        Assert.True(main.TrackLayout.IsDirty);
        main.TrackLayout.SaveCommand.Execute(null);
        Until(() => Layout(engine.State!.Settings).Tracks.Count == 3 && !main.TrackLayout.IsDirty, "the saved preset");
        Assert.Equal(["Everything", "Game and chat", "Microphone"], Layout(engine.State!.Settings).Tracks.Select(t => t.Name));

        // A fabricated Library: three complete MP4s, oldest first, in a chosen Clips folder.
        string[] names = ["alpha.mp4", "bravo.mp4", "charlie.mp4"];
        for (var i = 0; i < names.Length; i++)
        {
            var file = Path.Combine(output, names[i]);
            File.WriteAllBytes(file, [0, 0, 0, 16, .. "ftypisom"u8, 0, 0, 2, 0, 0, 0, 0, 8, .. "moov"u8]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(i - 3));
        }

        main.LibrarySettings.Confirm = _ => true;
        main.LibrarySettings.ClipsFolder.Choose(output);
        Until(() => engine.State!.LibraryFolder == output && engine.State.Library.Count == 3, "the fabricated Library");

        var shell = new RecordingShell();
        var library = new LibraryPage(engine, shell: shell, confirmDelete: _ => true);
        engine.Changed += () => post(() => library.Render(engine.State!));
        library.Render(engine.State!);
        Assert.False(library.ShowsWelcome);

        // R4: sort and search.
        (library.GroupBy, library.Sort) = (LibraryGrouping.None, LibrarySort.NewestFirst);
        Assert.Equal(["charlie.mp4", "bravo.mp4", "alpha.mp4"], Shown(library));
        library.Sort = LibrarySort.Name;
        Assert.Equal(names, Shown(library));
        library.SearchText = "RAV";
        Assert.Equal(["bravo.mp4"], Shown(library));
        library.ClearSearchCommand.Execute(null);

        // R1, R2: a mouse selection of two, then a bulk Favourite.
        library.Click(Row(library, "alpha.mp4"), ctrl: false, shift: false);
        library.Click(Row(library, "bravo.mp4"), ctrl: true, shift: false);
        library.FavouriteSelectionCommand.Execute(null);
        Until(() => engine.State!.Library.Count(i => i.Favourite) == 2, "the bulk Favourite");
        Until(() => Row(library, "alpha.mp4").IsFavourite && Row(library, "bravo.mp4").IsFavourite, "the stars");

        // Copy and delete act on the whole shown selection; the Favourites are kept.
        library.SelectAll();
        library.CopyCommand.Execute(null);
        Assert.Equal(names.Select(n => Path.Combine(output, n)), Assert.Single(shell.Copied).Order(StringComparer.Ordinal));
        library.DeleteCommand.Execute(null);
        Until(() => engine.State!.Library.Count == 2, "the delete");
        Assert.Equal(["alpha.mp4", "bravo.mp4"], engine.State!.Library.Select(i => i.Path).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(output, "charlie.mp4")));
        Assert.True(File.Exists(Path.Combine(output, "alpha.mp4")) && File.Exists(Path.Combine(output, "bravo.mp4")));

        // R9: a game's page, its Clip length changed for the game and reset to the main setting.
        Run(engine.SendAsync(SettingsMessages.Update, new SettingsUpdate(
            engine.State!.Settings.Revision,
            [SettingsOp.SetProfile(GameId, ProfileAttributes.Name, JsonSerializer.SerializeToElement("Demo"))])));
        Until(() => main.GameProfiles.Profiles.Any(p => p.ProfileId == GameId), "the game in Games");
        main.GameProfiles.Open(main.GameProfiles.Profiles.Single(p => p.ProfileId == GameId));
        var game = main.GameProfiles.OpenGame!;
        Assert.Equal("Uses the main setting", game.Clips.Chip);
        game.Clips.ChangeCommand.Execute(null);
        Until(() => Override(engine, Keys.BufferWindow.Name) is not null, "the game's own Clip length");
        game.Output.BufferWindowText = "45";
        Until(() => Override(engine, Keys.BufferWindow.Name) == "45", "the game's Clip length of 45 s");
        Until(() => game.Clips.IsChanged, "the changed chip");

        game.Confirm = (_, _) => true;
        game.Clips.ResetCommand.Execute(null);
        Until(() => Override(engine, Keys.BufferWindow.Name) is null && !game.Clips.IsChanged, "the reset");
        Assert.Equal("Uses the main setting", game.Clips.Chip);
    });

    private static TrackLayout Layout(SettingsDocument document)
    {
        Assert.True(Keys.TrackLayout.TryRead(document.Global[Keys.TrackLayout.Name], out var layout));
        return layout;
    }

    private static string? Override(EngineConnection engine, string key) =>
        engine.State!.Settings.Profiles.TryGetValue(GameId, out var profile) && profile.Overrides.TryGetValue(key, out var value) ? value.GetRawText() : null;

    private static List<string> Shown(LibraryPage page) => [.. page.Entries.OfType<LibraryRow>().Select(r => r.Name)];

    private static LibraryRow Row(LibraryPage page, string name) => page.Rows.Single(r => r.Name == name);

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds.</summary>
    private static void Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, $"Timed out waiting for {what}.");
            Sta.Pump(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void Run(Task task)
    {
        Until(() => task.IsCompleted, "a reply");
        task.GetAwaiter().GetResult();
    }

    /// <summary>The engine on fake endpoints and a Recycle Bin that deletes, and the App's connection to it.</summary>
    private sealed class Session : IDisposable
    {
        private readonly EngineHost _host;

        private Session(EngineHost host, EngineConnection engine) => (_host, Engine) = (host, engine);

        public EngineConnection Engine { get; }

        public static Session Start(Instance instance, string folder)
        {
            var host = EngineHost.Start(
                instance,
                NullLoggerFactory.Instance,
                hostOptions: new EngineHostOptions
                {
                    Heartbeat = null,
                    ProbeEncoders = false,
                    AppLauncher = new StayingLauncher(),
                    ReplayBuffer = false,
                    AudioEndpoints = new Endpoints(Speakers, Headset, Mic),
                    SeedTrackLayout = true,
                    Recycle = path =>
                    {
                        File.Delete(path);
                        return true;
                    },
                });
            var engine = new EngineConnection(new EngineConnectionOptions
            {
                InstanceName = instance.Name,
                EnginePath = Path.Combine(folder, "no-engine.exe"),
                TrustedEnginePath = Environment.ProcessPath!,
            });
            Run(engine.ConnectAsync());
            Assert.Equal(EngineStatus.Connected, engine.Status);
            return new Session(host, engine);
        }

        public void Dispose()
        {
            try
            {
                Run(Engine.SendAsync(EngineMessages.Shutdown, Empty.Value));
            }
            catch (Exception e) when (e is ErrorCodeException or IOException or OperationCanceledException)
            {
                // The engine may close the pipe before it replies.
            }

            Run(Engine.DisposeAsync().AsTask());
            Run(_host.DisposeAsync().AsTask());
        }
    }

    private sealed class Endpoints(params AudioDevice[] present) : IAudioEndpointSource
    {
        public event Action? EndpointsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AudioDevice> ListPresent() => present;
    }

    private sealed class RecordingShell : ILibraryShell
    {
        public List<IReadOnlyList<string>> Copied { get; } = [];

        public void Open(string path)
        {
        }

        public void ShowInExplorer(string folder, IReadOnlyList<string> files)
        {
        }

        public void OpenFolder(string folder)
        {
        }

        public void Copy(IReadOnlyList<string> paths) => Copied.Add(paths);

        public void CopyToFolder(IReadOnlyList<string> paths, string folder)
        {
        }
    }

    private sealed class StayingLauncher : IAppLauncher
    {
        public IWatchedProcess? Open(int pid) => new Staying(pid);

        public IWatchedProcess Start() => new Staying(0);

        private sealed class Staying(int id) : IWatchedProcess
        {
            public int Id { get; } = id;

            public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;

            public void Dispose()
            {
            }
        }
    }
}
