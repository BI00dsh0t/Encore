using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Platform.Desktop;

namespace Encore.App.Tests;

/// <summary>With the real App and engine: settings and choices survive an App kill, an engine kill and a restart. Heavy.</summary>
[Collection(nameof(SupervisionCollection))]
public sealed class NothingToReselectPairTests : IAsyncDisposable
{
    private static readonly TimeSpan BackWithin = TimeSpan.FromSeconds(30);

    private readonly string _instance = Instance.TestPrefix + Guid.NewGuid().ToString("N");
    private readonly string _output = Directory.CreateTempSubdirectory("rb-reselect-pair-").FullName;
    private readonly HashSet<int> _pids = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AppExe => Path.Combine(AppContext.BaseDirectory, "Encore.App.exe");

    private Instance TestInstance => Instance.ForTest(_instance);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var client = await EngineClient.ConnectAsync(
                PipeName.ForCurrentUser(_instance),
                new EngineClientOptions { ConnectTimeout = TimeSpan.FromMilliseconds(500) });
            await client.SendAsync(EngineMessages.Shutdown, Empty.Value).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1000);
        }
        catch (Exception e) when (e is ErrorCodeException or TimeoutException)
        {
            // No engine left.
        }

        foreach (var pid in _pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.ProcessName is "Encore.App" or "Encore.Engine")
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException)
            {
                // Already gone.
            }
        }

        foreach (var folder in new[] { TestInstance.RoamingDataFolder, TestInstance.LocalDataFolder, _output })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task EveryPersistedValueSurvivesAnAppKillAnEngineKillAndAQuitThenARunKeyLaunch()
    {
        // Mic mute off from the last run, and a Clip on disk to make a Favourite.
        Directory.CreateDirectory(TestInstance.LocalDataFolder);
        await File.WriteAllTextAsync(Path.Combine(TestInstance.LocalDataFolder, "runtime.json"), """{"micMuted":false}""", Token);
        Directory.CreateDirectory(Path.Combine(_output, "Game"));
        await File.WriteAllBytesAsync(Path.Combine(_output, "Game", "Game_2026-11-04_10-00-00.mp4"), CompleteMp4(), Token);

        var app = StartApp("--tray", "--instance", _instance);
        await WaitForStateAsync(s => s.AppPid == app.Id, BackWithin);
        var profileId = Path.Combine(_output, "game.exe").ToLowerInvariant();
        var game = new LayoutDevice(Guid.NewGuid(), "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-0000000000a1}", "Game", "BEACN", AudioFlow.Output, IsMic: false);
        var layout = new TrackLayout([game], [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false)]);
        await using (var client = await ConnectAsync())
        {
            var update = new SettingsUpdate(
                client.State.Settings.Revision,
                [
                    SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_output)),
                    SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
                    SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(45)),
                    SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(0x86, 7), null, null, null, null, null))),
                    SettingsOp.SetProfile(profileId, ProfileAttributes.Name, JsonSerializer.SerializeToElement("Game")),
                    SettingsOp.SetProfile(profileId, ProfileAttributes.RecordingsEnabled, JsonSerializer.SerializeToElement(true)),
                    SettingsOp.SetProfile(profileId, Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(20)),
                ]);
            await client.SendAsync(SettingsMessages.Update, update, Token);
        }

        var listed = await WaitForStateAsync(s => s.LibraryFolder == _output && s.Library.Count == 1, BackWithin);
        await using (var client = await ConnectAsync())
        {
            var item = listed.Library[0];
            await client.SendAsync(LibraryMessages.SetFavourite, new LibrarySetFavourite(item.Path, item.Size, item.Modified, true), Token);
        }

        var expected = await WaitForStateAsync(s => s.Library is [{ Favourite: true }], BackWithin);

        // The engine saves library.json on its own writer after the event: wait for the Favourite on disk.
        var clock = Stopwatch.StartNew();
        while (!LibraryEntries().Contains("\"favourite\":true", StringComparison.Ordinal))
        {
            Assert.True(clock.Elapsed < BackWithin, "library.json never held the Favourite");
            await Task.Delay(100, Token);
        }

        var files = Snapshot();

        // 1. The App is killed; the engine relaunches it.
        var engine = expected.Pid;
        Kill(app.Id);
        var state = await WaitForStateAsync(s => s.Pid == engine && s.AppPid is { } pid && pid != app.Id, BackWithin);
        AssertUnchanged(expected, state, files);

        // 2. The engine is killed; the App relaunches it, and the new engine starts from disk.
        Kill(engine);
        state = await WaitForStateAsync(s => s.Pid != engine && s.AppPid is not null, BackWithin);
        AssertUnchanged(expected, state, files);

        // 3. Quit from the tray (engine.shutdown ends both), then a sign-in's launch from the Run key's command line.
        using (var engineProcess = Process.GetProcessById(state.Pid))
        using (var appProcess = Process.GetProcessById(state.AppPid!.Value))
        {
            await using (var client = await ConnectAsync())
            {
                await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);
            }

            await engineProcess.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            await appProcess.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        // The Run value is written only for the installed instance, so the test instance's argument is appended.
        var command = new StartupRegistration(Instance.ForTest(_instance), new RunKey($@"Software\{_instance}\Run", "Encore"), AppExe).Command;
        Assert.StartsWith($"\"{AppExe}\" ", command, StringComparison.Ordinal);
        app = StartApp([.. command[(AppExe.Length + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries), "--instance", _instance]);
        state = await WaitForStateAsync(s => s.AppPid == app.Id, BackWithin);
        AssertUnchanged(expected, state, files);
    }

    private void AssertUnchanged(StateSnapshot expected, StateSnapshot state, IReadOnlyDictionary<string, string> files)
    {
        Assert.Equal(Json(expected.Settings), Json(state.Settings));
        Assert.False(state.Capture.MicMuted);
        Assert.True(Assert.Single(state.Library).Favourite);
        Assert.Contains(state.Hotkeys.Bindings, b => b.Action == HotkeyAction.Save && b.Binding == new HotkeyBinding(0x86, 7));
        Assert.Equal(files, Snapshot());
    }

    /// <summary>64 bytes the Library's scan lists: an <c>ftyp</c> box then a <c>moov</c> box; zeros alone aren't an MP4.</summary>
    private static byte[] CompleteMp4()
    {
        var bytes = new byte[64];
        void Box(int at, int size, string type)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)size);
            System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(at + 4));
        }

        Box(0, 16, "ftyp");
        Box(16, 48, "moov");
        return bytes;
    }

    private static string Json(SettingsDocument document) => JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument);

    private static void Kill(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
    }

    /// <summary>The bytes of the settings and runtime files, and the Library's parsed items, as the engine may re-save its file.</summary>
    private Dictionary<string, string> Snapshot() => new()
    {
        ["settings.json"] = Convert.ToBase64String(ReadShared(Path.Combine(TestInstance.RoamingDataFolder, "settings.json"))),
        ["runtime.json"] = Convert.ToBase64String(ReadShared(Path.Combine(TestInstance.LocalDataFolder, "runtime.json"))),
        ["library.json"] = LibraryEntries(),
    };

    /// <summary>Each saved item's path, Game Profile, size and Favourite, one line per item.</summary>
    private string LibraryEntries()
    {
        using var document = JsonDocument.Parse(ReadShared(Path.Combine(TestInstance.RoamingDataFolder, "library.json")));
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(i => string.Join(
            ",",
            i.GetProperty("path").GetString(),
            i.TryGetProperty("profileId", out var profile) ? profile.GetString() : "",
            i.TryGetProperty("size", out var size) ? size.GetRawText() : "",
            "\"favourite\":" + (i.TryGetProperty("favourite", out var favourite) && favourite.GetBoolean() ? "true" : "false")));
        return string.Join(";", items);
    }

    /// <summary>A file the engine may be replacing right now: read with sharing, retried for up to 2 s.</summary>
    private static byte[] ReadShared(string path)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(50);
            }
        }
    }

    private Process StartApp(params string[] args)
    {
        var start = new ProcessStartInfo(AppExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start)!;
        _pids.Add(process.Id);
        return process;
    }

    private async Task<EngineClient> ConnectAsync(TimeSpan? timeout = null)
    {
        var client = await EngineClient.ConnectAsync(
            PipeName.ForCurrentUser(_instance),
            new EngineClientOptions { ConnectTimeout = timeout ?? TimeSpan.FromSeconds(5) },
            Token);
        _pids.Add(client.State.Pid);
        return client;
    }

    /// <summary>Connects afresh until a snapshot satisfies <paramref name="condition"/>, within <paramref name="within"/>.</summary>
    private async Task<StateSnapshot> WaitForStateAsync(Func<StateSnapshot, bool> condition, TimeSpan within)
    {
        var elapsed = Stopwatch.StartNew();
        StateSnapshot? last = null;
        while (elapsed.Elapsed < within)
        {
            try
            {
                await using var client = await ConnectAsync(TimeSpan.FromMilliseconds(500));
                last = client.State;
                if (last.AppPid is { } appPid)
                {
                    _pids.Add(appPid);
                }

                if (condition(last))
                {
                    return last;
                }
            }
            catch (ErrorCodeException)
            {
                // Between engines.
            }

            await Task.Delay(100, Token);
        }

        Assert.Fail($"No snapshot matched within {within.TotalSeconds:0} s; the last was {last}.");
        return null!;
    }
}
