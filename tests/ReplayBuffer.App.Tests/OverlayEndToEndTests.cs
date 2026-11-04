using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Feedback;
using ReplayBuffer.Engine;
using ReplayBuffer.Engine.Supervision;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// Story 5.14's closing check of epic 5, unattended: the whole engine (<c>EngineHost</c>) in-process on a throwaway
/// test instance with the Replay Buffer on, the real foreground Game Detection, the real pipe and Hotkeys, and the
/// App's own <see cref="EngineConnection"/>, <see cref="OverlayViewModel"/>, <see cref="OverlayWindow"/> and
/// <see cref="MainViewModel"/> status line on it, wired as <c>ShellApp</c> wires them (the real plane query, the
/// capture-exclusion hook). Bench's borderless GPU load is the Target Game. Injected Save and Mic Mute presses and
/// <c>dev.simulate</c> drive it. The Feedback Sounds go to a recording output: requested, never played aloud.
/// Needs the GPU, an unlocked console and the Release build of the GPU load.
/// </summary>
[Collection(nameof(WpfCollection))]
public sealed class OverlayEndToEndTests : IDisposable
{
    private const int VkF22 = 0x85;
    private const int VkF21 = 0x84;
    private const int Combo = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
    private const int BufferWindow = 5;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-e2e-overlay-").FullName;

    private static ITestOutputHelper? Log => TestContext.Current.TestOutputHelper;

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

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TheOverlayStatusLineAndSoundsFollowTheRealEngineOverTheLoad() => Sta.Run(() =>
    {
        var loadExe = GpuLoadExe();
        Assert.SkipUnless(loadExe is not null, "the GPU load is not built (dotnet build ReplayBuffer.slnx -c Release)");
        var clips = Directory.CreateDirectory(Path.Combine(_folder, "clips")).FullName;
        var instance = new Instance(Instance.TestPrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        var sounds = new RecordingSoundOutput();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var host = EngineHost.Start(
            instance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, AppLauncher = new StayingLauncher(), ReplayBuffer = true, FeedbackSounds = sounds });
        var engine = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = instance.Name,
            EnginePath = Path.Combine(_folder, "no-engine.exe"),
            TrustedEnginePath = Environment.ProcessPath!,
        });
        IDisposable? exclusion = null;
        OverlayWindow? overlay = null;
        LoadProcess? load = null;
        try
        {
            Run(engine.ConnectAsync());
            Assert.Equal(EngineStatus.Connected, engine.Status);
            exclusion = CaptureExclusion.ExcludeNewWindows();
            var model = new OverlayViewModel(engine, action => dispatcher.BeginInvoke(action), overlayPlanes: Monitors.OverlayPlanes, background: action => Task.Run(action));
            overlay = new OverlayWindow(model);
            var main = new MainViewModel(engine, action => dispatcher.BeginInvoke(action));

            Update(engine,
                SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(clips)),
                SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(BufferWindow)),
                SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(5)),
                SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(VkF22, Combo), new HotkeyBinding(VkF21, Combo), null, null, null, null))));
            Until(() => engine.State!.Hotkeys.Bindings.Count(b => b.Registered) == 2, "the Save and Mic Mute Hotkeys registered");
            Assert.False(overlay.IsVisible);

            // R1: the load becomes the Target Game; the hollow dot within 1 s of buffering. A test instance has no
            // runtime.json, so the mic starts muted and the mic-muted icon shows beside the dot.
            load = StartLoad(loadExe!);
            var buffering = Until(() => engine.State!.Capture.Replay == ReplayState.Buffering, "the Replay Buffer buffering on the load");
            var dotIn = Until(() => model.Dot == OverlayDot.Hollow && overlay.IsVisible, "the hollow dot");
            Log?.WriteLine($"dot {dotIn.TotalMilliseconds:0} ms after buffering was seen; DotAlways {model.DotAlways}; planes {Monitors.OverlayPlanes(engine.State!.TargetGame?.ProcessId)?.ToString() ?? "unknown"}");
            Assert.InRange(dotIn, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.True(model.MicMuted);
            Assert.Equal(load.MainWindow, OverlayWindowStyle.ForegroundWindow());
            Assert.Equal($"Buffering on {engine.State!.TargetGame!.Name} · Mic muted", main.StatusLine.State);

            // The adaptive dot (5.15): with no overlay planes, auto shows it for 3 s after each change only.
            if (!model.DotAlways)
            {
                var hidden = Until(() => model.Dot == OverlayDot.None, "the dot hiding after its 3 s");
                Log?.WriteLine($"onChange dot hid {hidden.TotalMilliseconds:0} ms after it showed");
                Assert.InRange(hidden.TotalSeconds, 1.5, OverlayViewModel.DotOnChangeDuration.TotalSeconds + 1);
                Assert.False(model.MicMuted);
            }

            // R3: the Mic Mute press turns the icon off; with onChange the change shows the dot again.
            Press(VkF21);
            Until(() => !engine.State!.Capture.MicMuted, "mic unmuted");
            Until(() => !model.MicMuted && model.Dot == OverlayDot.Hollow, "the dot without the mic-muted icon");
            Assert.Equal($"Buffering on {engine.State!.TargetGame!.Name}", main.StatusLine.State);

            // R2: a Save press shows the saving icon within 250 ms and keeps it through the cooldown and the write.
            Pump(TimeSpan.FromSeconds(BufferWindow + 1));
            var before = engine.State!.LastClip;
            var press = Stopwatch.StartNew();
            Press(VkF22);
            Until(() => model.Saving, "the saving icon");
            var savingIn = press.Elapsed;
            Log?.WriteLine($"saving icon {savingIn.TotalMilliseconds:0} ms after the press");
            Assert.InRange(savingIn, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
            Until(() => engine.State!.LastClip != before, "clip.saved");
            Assert.True(File.Exists(engine.State!.LastClip!.Path));
            Press(VkF22);
            Pump(TimeSpan.FromSeconds(1));
            Assert.True(model.Saving);
            Until(() => !model.Saving, "the saving icon ending with the cooldown");
            Log?.WriteLine($"saving icon held {press.Elapsed.TotalSeconds:0.0} s");
            Assert.InRange(press.Elapsed.TotalSeconds, 4.5, 7);
            Assert.Single(Directory.GetFiles(clips, "*.mp4", SearchOption.AllDirectories));

            // R3: messages queue, oldest first, never overwrite. The saved Clip's message showed first; a failed save
            // and a disk full arrive together and show one after the other.
            Simulate(engine, SimulatedEvent.SaveFailed);
            Simulate(engine, SimulatedEvent.DiskFull);
            var codes = new List<string>();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(14))
            {
                if (model.Message is { } m && (codes.Count == 0 || codes[^1] != m.Code))
                {
                    codes.Add(m.Code);
                }

                Pump(TimeSpan.FromMilliseconds(50));
            }

            Log?.WriteLine("messages: " + string.Join(", ", codes));
            Assert.Equal([ErrorCodes.ClipFailed, ErrorCodes.DiskFull], codes.Where(c => c != ErrorCodes.ClipSaved).Take(2));

            // R3: the red X shows while disk-full is active; the status line lists it newest first, and its Dismiss
            // (warning.dismiss) clears the red X.
            Assert.True(model.DiskFull);
            var diskFull = Assert.Single(main.StatusLine.Warnings, w => w.Message == "The disk is full");
            Assert.Same(diskFull, main.StatusLine.Warnings[0]);
            Assert.True(diskFull.CanDismiss);
            diskFull.DismissCommand.Execute(null);
            Until(() => !model.DiskFull, "the red X gone after Dismiss");

            // R1: the stub Recording shows the solid dot within 1 s, and its stop brings back the hollow one.
            var start = Stopwatch.StartNew();
            Simulate(engine, SimulatedEvent.RecordingStarted);
            Until(() => model.Dot == OverlayDot.Solid, "the solid dot");
            Assert.InRange(start.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.Contains("Recording", main.StatusLine.State, StringComparison.Ordinal);
            var stop = Stopwatch.StartNew();
            Simulate(engine, SimulatedEvent.RecordingStopped);
            Until(() => model.Dot == OverlayDot.Hollow, "the hollow dot after the Recording");
            Assert.InRange(stop.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            // R6: every window the App shows is excluded from capture, the Overlay included, and the game kept focus.
            Assert.True(overlay.IsVisible);
            var windows = CaptureExclusion.VisibleWindows();
            Assert.Contains(overlay.Handle, windows);
            Assert.All(windows, w => Assert.True(CaptureExclusion.IsApplied(w), $"Window {w:X} isn't excluded."));
            Assert.Equal(load.MainWindow, OverlayWindowStyle.ForegroundWindow());

            // R4: each notice requested its sound on the default communications endpoint, and nothing played aloud.
            Until(() => sounds.Played.Count >= 5, "five Feedback Sounds");
            Log?.WriteLine("sounds: " + string.Join(", ", sounds.Played));
            Assert.Equal(
                [FeedbackSound.ClipSaved, FeedbackSound.SaveFailed, FeedbackSound.DiskFull, FeedbackSound.RecordingStarted, FeedbackSound.RecordingStopped],
                sounds.Played);
            Assert.All(sounds.Endpoints, e => Assert.Equal(RecordingSoundOutput.Endpoint, e));

            Run(engine.SendAsync(EngineMessages.Shutdown, Empty.Value));
        }
        finally
        {
            overlay?.Close();
            exclusion?.Dispose();
            load?.Dispose();
            Run(engine.DisposeAsync().AsTask());
            Run(host.DisposeAsync().AsTask());
        }
    });

    private static void Press(int vk) => Keys2.Press(vk, Combo);

    private static void Simulate(EngineConnection engine, SimulatedEvent simulated) =>
        Run(engine.SendAsync(DevMessages.Simulate, new DevSimulate(simulated)));

    private static void Update(EngineConnection engine, params SettingsOp[] ops)
    {
        var result = Run(engine.SendAsync(SettingsMessages.Update, new SettingsUpdate(engine.State!.Settings.Revision, ops)));
        Until(() => engine.State!.Settings.Revision >= result.Revision, "settings.changed");
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds; returns how long that took.</summary>
    private static TimeSpan Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, $"Timed out waiting for {what}.");
            Pump(TimeSpan.FromMilliseconds(10));
        }

        return clock.Elapsed;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Waits for <paramref name="task"/> while the dispatcher keeps running.</summary>
    private static T Run<T>(Task<T> task)
    {
        Until(() => task.IsCompleted, "a reply");
        return task.GetAwaiter().GetResult();
    }

    private static void Run(Task task)
    {
        Until(() => task.IsCompleted, "a reply");
        task.GetAwaiter().GetResult();
    }

    /// <summary>The GPU load from the Release build of the repo this test assembly sits in, or null.</summary>
    private static string? GpuLoadExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReplayBuffer.slnx")))
            {
                var bin = new DirectoryInfo(Path.Combine(dir.FullName, "bench", "ReplayBuffer.Bench.GpuLoad", "bin", "Release"));
                return bin.Exists
                    ? bin.GetFiles("ReplayBuffer.Bench.GpuLoad.exe", SearchOption.AllDirectories).OrderBy(f => f.LastWriteTimeUtc).LastOrDefault()?.FullName
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
            Log?.WriteLine("load: " + line);
            if (line.StartsWith("ready", StringComparison.Ordinal))
            {
                process.Refresh();
                return new LoadProcess(process, process.MainWindowHandle);
            }

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the GPU load never said ready");
        }

        process.Dispose();
        Assert.Fail("the GPU load exited before it was ready");
        return null!;
    }

    private sealed class LoadProcess(Process process, nint mainWindow) : IDisposable
    {
        public nint MainWindow { get; } = mainWindow;

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
                // It already exited.
            }

            process.Dispose();
        }
    }

    /// <summary>Records which Feedback Sound each play asked for; plays nothing.</summary>
    private sealed class RecordingSoundOutput : IFeedbackSoundOutput
    {
        public const string Endpoint = "e2e-test-endpoint";

        private static readonly (FeedbackSound Sound, float[] Pcm)[] Rendered =
            [.. Enum.GetValues<FeedbackSound>().Select(s => (s, FeedbackSoundSynth.Render(s).ToArray()))];

        private readonly Lock _lock = new();
        private readonly List<FeedbackSound> _played = [];
        private readonly List<string> _endpoints = [];

        public IReadOnlyList<FeedbackSound> Played
        {
            get
            {
                lock (_lock)
                {
                    return [.. _played];
                }
            }
        }

        public IReadOnlyList<string> Endpoints
        {
            get
            {
                lock (_lock)
                {
                    return [.. _endpoints];
                }
            }
        }

        public string? DefaultEndpointId() => Endpoint;

        public void Play(string endpointId, ReadOnlyMemory<float> pcm)
        {
            var sound = Rendered.Single(r => pcm.Span.SequenceEqual(r.Pcm)).Sound;
            lock (_lock)
            {
                _played.Add(sound);
                _endpoints.Add(endpointId);
            }
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

    /// <summary>Injects a Hotkey press: the modifiers down, the key down and up, the modifiers up.</summary>
    private static class Keys2
    {
        private const uint InputKeyboard = 1;
        private const uint KeyUp = 0x0002;

        public static void Press(int vk, int modifiers)
        {
            var mods = new List<ushort>();
            if ((modifiers & HotkeyModifiers.Control) != 0)
            {
                mods.Add(0x11);
            }

            if ((modifiers & HotkeyModifiers.Alt) != 0)
            {
                mods.Add(0x12);
            }

            if ((modifiers & HotkeyModifiers.Shift) != 0)
            {
                mods.Add(0x10);
            }

            Send([.. mods.Select(m => Key(m, up: false)), Key((ushort)vk, up: false), Key((ushort)vk, up: true), .. Enumerable.Reverse(mods).Select(m => Key(m, up: true))]);
        }

        private static void Send(Input[] inputs)
        {
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            Assert.Equal((uint)inputs.Length, sent);
        }

        private static Input Key(ushort vk, bool up) => new() { Type = InputKeyboard, Keyboard = new KeyboardInput { Vk = vk, Flags = up ? KeyUp : 0 } };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

        /// <summary><c>INPUT</c> on x64: the type, then the union at offset 8, 40 bytes in all.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)]
            public uint Type;

            [FieldOffset(8)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }
    }
}
