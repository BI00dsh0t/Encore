using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Engine.Supervision;
using Encore.Platform.Windows;

namespace Encore.Engine.Tests;

/// <summary>From the Save Hotkey to a Clip through the whole engine, in Memory and Disk mode. Needs the GPU and a console.</summary>
[Collection(nameof(HotkeyServeCollection))]
public sealed class ClipsEndToEndTests : IDisposable
{
    private const int VkF22 = 0x85;
    private const int Combo = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
    private const int FirstWindow = 8;
    private const int NextWindow = 3;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly VideoSettings GlobalVideo = new(VideoCodec.H264, 1280, 720, 30, 6_000, RateControl.Cbr);
    private static readonly VideoSettings GameVideo = GlobalVideo with { Width = 640, Height = 360 };

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-e2e-clips-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A ring file may be released a moment late; the temp folder is left behind.
        }
    }

    [Theory(Explicit = true)]
    [Trait("Category", "Heavy")]
    [InlineData(ReplayStorage.Memory)]
    [InlineData(ReplayStorage.Disk)]
    public async Task SavePressesWriteClipsThatFollowTheSettingsFromTheNextReset(ReplayStorage storage)
    {
        SessionTestKit.RequireFFmpeg();
        var loadExe = GpuLoadExe();
        Assert.SkipUnless(loadExe is not null, "the GPU load is not built (dotnet build Encore.slnx -c Release)");
        var clips = Directory.CreateDirectory(Path.Combine(_folder, "clips")).FullName;
        var ring = Directory.CreateDirectory(Path.Combine(_folder, "ring")).FullName;
        var instance = new Instance(Instance.TestPrefix + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

        await using var host = EngineHost.Start(
            instance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, AppLauncher = new StayingLauncher(), Rewind = true });
        await using var client = await EngineClient.ConnectAsync(host.PipeName, cancellationToken: Token);
        await UpdateAsync(client,
            SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(clips)),
            SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(GlobalVideo)),
            SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(FirstWindow)),
            SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(5)),
            SettingsOp.SetGlobal(Keys.ReplayStorage.Name, Keys.ReplayStorage.ToJson(storage)),
            SettingsOp.SetGlobal(Keys.RingPath.Name, Keys.RingPath.ToJson(ring)),
            SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(VkF22, Combo), null, null, null, null, null))));
        await UntilAsync(() => client.State.Hotkeys.Bindings.FirstOrDefault(b => b.Action == HotkeyAction.Save)?.Registered == true, "the Save Hotkey registered");

        // The load fills the monitor borderless, so Game Detection makes it the Target Game.
        using var load = StartLoad(loadExe!);
        var profileId = GamePaths.Canonicalize(loadExe!);
        await UntilAsync(() => client.State.Capture.Replay == ReplayState.Buffering, "Rewind buffering on the load");
        Assert.Equal(profileId, client.State.TargetGame?.ProfileId);
        if (storage == ReplayStorage.Disk)
        {
            Assert.NotEmpty(Directory.GetFiles(ring, "ring-*.bin", SearchOption.AllDirectories));
        }

        await Task.Delay(TimeSpan.FromSeconds(FirstWindow + 2), Token);
        var first = await SaveAsync(client);
        var firstSaved = Stopwatch.StartNew();
        Check(first, GlobalVideo, FirstWindow - 1.5, FirstWindow + 0.1);

        // Inside the 5 s Save Cooldown: no Clip and no clip.saving.
        var saving = client.State.LastClipSaving;
        HotkeyServeTests.Input.Press(VkF22, Combo);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
        Assert.Equal(saving, client.State.LastClipSaving);
        Assert.Single(Directory.GetFiles(clips, "*.mp4", SearchOption.AllDirectories));

        // Both changes take effect at the Reset after the next save.
        await UpdateAsync(client,
            SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(NextWindow)),
            SettingsOp.SetProfile(profileId, Keys.Video.Name, Keys.Video.ToJson(GameVideo)));
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, 6.5 - firstSaved.Elapsed.TotalSeconds)), Token);
        var second = await SaveAsync(client);
        Check(second, GlobalVideo, 4, FirstWindow + 0.1);

        await Task.Delay(TimeSpan.FromSeconds(6.5), Token);
        var third = await SaveAsync(client);
        Check(third, GameVideo, NextWindow - 1.5, NextWindow + 0.1);
        Assert.Equal(3, Directory.GetFiles(clips, "*.mp4", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(clips, "*.rbpart", SearchOption.AllDirectories));

        await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);
    }

    /// <summary>A reboot, as two cold engine starts on the same folders: Rewind starts by itself and a press saves a Clip.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task TheRewindStartsOnTheGameByItselfAfterEachColdStart()
    {
        SessionTestKit.RequireFFmpeg();
        var loadExe = GpuLoadExe();
        Assert.SkipUnless(loadExe is not null, "the GPU load is not built (dotnet build Encore.slnx -c Release)");
        var clips = Directory.CreateDirectory(Path.Combine(_folder, "clips")).FullName;
        var instance = new Instance(Instance.TestPrefix + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        var options = new EngineHostOptions { Heartbeat = null, AppLauncher = new StayingLauncher(), Rewind = true };

        await using (var host = EngineHost.Start(instance, NullLoggerFactory.Instance, hostOptions: options))
        await using (var client = await EngineClient.ConnectAsync(host.PipeName, cancellationToken: Token))
        {
            await UpdateAsync(client,
                SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(clips)),
                SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(GlobalVideo)),
                SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(NextWindow)),
                SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1)),
                SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(VkF22, Combo), null, null, null, null, null))));
            await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);
        }

        using var load = StartLoad(loadExe!);
        for (var start = 1; start <= 2; start++)
        {
            await using var host = EngineHost.Start(instance, NullLoggerFactory.Instance, hostOptions: options);
            await using var client = await EngineClient.ConnectAsync(host.PipeName, cancellationToken: Token);
            await UntilAsync(() => client.State.Capture.Replay == ReplayState.Buffering, $"buffering after cold start {start}");
            Assert.Equal(GamePaths.Canonicalize(loadExe!), client.State.TargetGame?.ProfileId);
            await UntilAsync(() => client.State.Hotkeys.Bindings.FirstOrDefault(b => b.Action == HotkeyAction.Save)?.Registered == true, "the Save Hotkey registered");
            await Task.Delay(TimeSpan.FromSeconds(NextWindow + 2), Token);
            Check(await SaveAsync(client), GlobalVideo, NextWindow - 1.5, NextWindow + 0.1);
            await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);
        }

        Assert.Equal(2, Directory.GetFiles(clips, "*.mp4", SearchOption.AllDirectories).Length);
    }

    /// <summary>Presses Save and returns the new Clip's path.</summary>
    private static async Task<string> SaveAsync(EngineClient client)
    {
        var before = client.State.LastClip;
        var failure = client.State.LastClipFailure;
        HotkeyServeTests.Input.Press(VkF22, Combo);
        await UntilAsync(() => client.State.LastClip != before || client.State.LastClipFailure != failure, "clip.saved");
        Assert.True(client.State.LastClipFailure == failure, $"clip.failed: {client.State.LastClipFailure?.Reason}");
        var path = client.State.LastClip!.Path;
        TestContext.Current.TestOutputHelper?.WriteLine("saved " + path);
        Assert.True(File.Exists(path), path);
        return path;
    }

    /// <summary>ffprobe: the video's size and length, named audio Tracks within 50 ms of the video, and moov before mdat.</summary>
    private static void Check(string clip, VideoSettings video, double min, double max)
    {
        var probe = SessionTestKit.Probe(clip, "-show_entries", "stream=codec_type,width,height,duration:stream_tags=name");
        TestContext.Current.TestOutputHelper?.WriteLine(probe.ToString());
        var streams = probe.GetProperty("streams").EnumerateArray().ToList();
        var v = Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "video");
        Assert.Equal(video.Width, v.GetProperty("width").GetInt32());
        Assert.Equal(video.Height, v.GetProperty("height").GetInt32());
        var length = Seconds(v);
        Assert.InRange(length, min, max);
        var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToList();
        Assert.NotEmpty(audio);
        Assert.All(audio, a =>
        {
            Assert.True(a.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 }, "a Track name");
            Assert.InRange(Math.Abs(Seconds(a) - length), 0, 0.05);
        });

        Assert.True(MoovFirst(clip), "moov before mdat");
    }

    private static double Seconds(JsonElement stream) => double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);

    private static bool MoovFirst(string clip)
    {
        using var file = File.OpenRead(clip);
        var header = new byte[8];
        while (file.Read(header, 0, 8) == 8)
        {
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (type is "moov" or "mdat")
            {
                return type == "moov";
            }

            if (size == 1)
            {
                var large = new byte[8];
                file.ReadExactly(large);
                size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(large);
                file.Seek(size - 16, SeekOrigin.Current);
            }
            else
            {
                file.Seek(size - 8, SeekOrigin.Current);
            }
        }

        return false;
    }

    private static async Task UpdateAsync(EngineClient client, params SettingsOp[] ops)
    {
        var result = await client.SendAsync(SettingsMessages.Update, new SettingsUpdate(client.State.Settings.Revision, ops), Token);
        await UntilAsync(() => client.State.Settings.Revision >= result.Revision, "settings.changed");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, $"Timed out waiting for {what}.");
            await Task.Delay(50, Token);
        }
    }

    /// <summary>The GPU load from the Release build of the repo this test assembly sits in, or null.</summary>
    private static string? GpuLoadExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Encore.slnx")))
            {
                var bin = new DirectoryInfo(Path.Combine(dir.FullName, "bench", "Encore.Bench.GpuLoad", "bin", "Release"));
                return bin.Exists
                    ? bin.GetFiles("Encore.Bench.GpuLoad.exe", SearchOption.AllDirectories).OrderBy(f => f.LastWriteTimeUtc).LastOrDefault()?.FullName
                    : null;
            }
        }

        return null;
    }

    /// <summary>Starts the load at 60 fps and waits for its ready line; it closes with this process.</summary>
    private static LoadProcess StartLoad(string exe)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "--target-fps", "60", "--warmup", "2", "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start)!;
        process.BeginErrorReadLine();
        var clock = Stopwatch.StartNew();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("load: " + line);
            if (line.StartsWith("ready", StringComparison.Ordinal))
            {
                return new LoadProcess(process);
            }

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the GPU load never said ready");
        }

        process.Dispose();
        Assert.Fail("the GPU load exited before it was ready");
        return null!;
    }

    private sealed class LoadProcess(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }

    /// <summary>An App launcher whose Apps never exit.</summary>
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
