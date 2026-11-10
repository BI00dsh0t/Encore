using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Library;
using Encore.Core.Output;
using Encore.Core.Settings;
using Encore.Engine;
using Encore.Engine.Supervision;

namespace Encore.App.Tests;

/// <summary>
/// Story 6.13's closing check of epic 6, the parts that need no capture (the builder has paused GPU tests):
/// the whole engine (<c>EngineHost</c>) in-process on a throwaway test instance with Rewind off,
/// the App's own <see cref="EngineConnection"/> and the real <c>MainViewModel</c>'s pages. It walks the
/// Recordings settings through a game's page and the Hotkeys page (with its screen picker) and across an engine restart,
/// <c>monitors.list</c> on this PC, the Hotkeys rows each Record mode shows, Recording and Monitor Recording
/// names and folders from Core's allocator as the Library sees them, and an interrupted fragmented MP4
/// (made by <c>ffmpeg.exe</c> on the CPU) trimmed at the next start with <c>recording-interrupted</c>.
/// </summary>
[Collection(nameof(WpfCollection))]
public sealed class RecordingsEndToEndTests : IDisposable
{
    private const string ProfileId = @"c:\games\alpha\alpha.exe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-e2e-recordings-").FullName;

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
    public void RecordingsSettingsNamesAndAnInterruptedRecordingWorkEndToEndWithoutCapture() => Sta.Run(() =>
    {
        Assert.SkipUnless(File.Exists(Tool("ffmpeg.exe")) && File.Exists(Tool("ffprobe.exe")), "native/ffmpeg is not fetched");
        var output = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var fragmented = Fragmented("rec.mp4", 4);

        // A Guid name: a dotted temp-folder name isn't a test instance name, and the engine would then start
        // Rewind on the foreground window. Rewind is also off explicitly.
        var instance = new Instance(Instance.TestPrefix + "recordings-" + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        Assert.True(instance.IsTest);
        var logs = new ListLoggers();
        var dispatcher = Dispatcher.CurrentDispatcher;
        Action<Action> post = action => dispatcher.BeginInvoke(action);

        var recordStart = new HotkeyBinding(0x83, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift);
        var recordStop = new HotkeyBinding(0x84, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift);
        var monitorToggle = new HotkeyBinding(0x85, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift);
        IReadOnlyList<DisplayMonitor> monitors;
        using (var first = Session.Start(instance, _folder, post, logs))
        {
            var engine = first.Engine;
            var main = first.Main;

            // A Game Profile (a name op creates it, as detection would) and the Output Folder.
            var name = JsonDocument.Parse("\"Alpha\"").RootElement;
            Run(engine.SendAsync(SettingsMessages.Update, new SettingsUpdate(engine.State!.Settings.Revision, [
                SettingsOp.SetProfile(ProfileId, ProfileAttributes.Name, name),
                SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(output)),
            ])));
            Until(() => main.GameProfiles.Profiles.Any(p => p.ProfileId == ProfileId), "the profile on the Game Profiles page");

            // R9: Recordings are off by default; the Game Profiles page switches them on.
            var profile = main.GameProfiles.Page(ProfileId);
            Assert.False(profile.RecordingsEnabled);
            profile.RecordingsEnabled = true;
            Until(() => engine.State!.Settings.Profiles[ProfileId].RecordingsEnabled, "recordingsEnabled saved");

            // monitors.list on this PC: the primary first, and the Hotkeys screen picker lists each one.
            monitors = Run(engine.SendAsync(RecordingMessages.MonitorsList, Empty.Value)).Monitors;
            Log?.WriteLine("monitors: " + string.Join("; ", monitors.Select(m => $"{m.DeviceName} {m.FriendlyName} {m.Width}x{m.Height}{(m.Primary ? " primary" : "")}")));
            Assert.NotEmpty(monitors);
            Assert.True(monitors[0].Primary);
            Assert.Single(monitors, m => m.Primary);
            main.Hotkeys.Monitor.RefreshCommand.Execute(null);
            Until(() => main.Hotkeys.Monitor.Rows.Count == monitors.Count, "the Hotkeys screen picker's rows");
            Assert.Equal(monitors.Select(m => m.MonitorId ?? m.DeviceName), main.Hotkeys.Monitor.Rows.Select(r => r.Id));
            main.Hotkeys.Monitor.IsAllScreens = true;
            Until(() => Effective(engine).MonitorRecordingScreens?.Mode == MonitorScreensMode.All, "monitorRecordingScreens saved");
            Assert.Null(Effective(engine).MonitorRecordingMonitor);

            // The Hotkeys page: Toggle shows the toggle rows; a Separate key bound in Toggle mode is inactive.
            var hotkeys = main.Hotkeys;
            Assert.True(hotkeys.IsToggleMode);
            Assert.Equal(["Save Clip", "Mic mute", "Record", "Monitor Recording"], Shown(hotkeys));
            hotkeys.Capture(HotkeyAction.RecordStart, recordStart);
            Until(() => Status(engine, HotkeyAction.RecordStart) is { Binding: not null }, "Record start saved");
            hotkeys.Capture(HotkeyAction.RecordStop, recordStop);
            Until(() => Status(engine, HotkeyAction.RecordStop) is { Binding: not null }, "Record stop saved");
            hotkeys.Capture(HotkeyAction.MonitorRecordToggle, monitorToggle);
            Until(() => Status(engine, HotkeyAction.MonitorRecordToggle) is { Registered: true }, "Monitor Recording (toggle) registered");
            Until(() => Row(hotkeys, HotkeyAction.RecordStart).StatusText == "Not used in this Record mode", "Record start inactive in Toggle mode");
            Assert.Equal("Working", Row(hotkeys, HotkeyAction.MonitorRecordToggle).StatusText);

            // Separate shows the start and stop rows, and the engine now holds the Separate keys.
            hotkeys.IsSeparateMode = true;
            Until(() => Effective(engine).RecordHotkeyMode == RecordHotkeyMode.Separate, "recordHotkeyMode saved");
            Assert.Equal(["Save Clip", "Mic mute", "Record start", "Record stop", "Monitor Recording start", "Monitor Recording stop"], Shown(hotkeys));
            Until(() => Status(engine, HotkeyAction.RecordStart) is { Registered: true } && Status(engine, HotkeyAction.RecordStop) is { Registered: true }, "the Separate keys registered");
            Until(() => Row(hotkeys, HotkeyAction.RecordStart).StatusText == "Working", "Record start working");
            Assert.Equal("Not used in this Record mode", Row(hotkeys, HotkeyAction.MonitorRecordToggle).StatusText);
        }

        // The round trip: a new engine run reads every Recordings setting back, and the pages show them.
        using (var second = Session.Start(instance, _folder, post, logs))
        {
            var engine = second.Engine;
            var main = second.Main;
            Until(() => main.GameProfiles.Profiles.Any(p => p.ProfileId == ProfileId) && main.Hotkeys.Monitor.IsAllScreens, "the pages after a restart");
            Assert.True(main.GameProfiles.Page(ProfileId).RecordingsEnabled);
            Assert.True(main.Hotkeys.IsSeparateMode);
            Assert.Equal("Ctrl+Alt+Shift+F20", Row(main.Hotkeys, HotkeyAction.RecordStart).BindingText);
            Assert.Equal("Ctrl+Alt+Shift+F21", Row(main.Hotkeys, HotkeyAction.RecordStop).BindingText);
            var effective = Effective(engine);
            Assert.Equal((recordStart, recordStop, monitorToggle), (effective.Hotkeys.RecordStart, effective.Hotkeys.RecordStop, effective.Hotkeys.MonitorRecordToggle));
            Assert.Equal(RecordHotkeyMode.Separate, effective.RecordHotkeyMode);

            // Recording names and folders (R26, AD-21): the allocator the Recordings use, on the engine's document.
            var document = engine.State!.Settings;
            Assert.Equal("Alpha", document.Profiles[ProfileId].Folder);
            var qpc = Stopwatch.GetTimestamp();
            var game = OutputPathAllocator.System.AllocateForGame(document, ProfileId, "alpha.exe", output, qpc);
            var again = OutputPathAllocator.System.AllocateForGame(document, ProfileId, "alpha.exe", output, qpc);
            var screen = OutputPathAllocator.System.AllocateForMonitor(output, qpc);
            Log?.WriteLine($"paths: {game}; {again}; {screen}");
            Assert.Equal(Path.Combine(output, "Alpha"), Path.GetDirectoryName(game));
            Assert.Matches(Stamp("Alpha", ""), Path.GetFileName(game));
            Assert.Equal(Path.ChangeExtension(game, null) + "_2.mp4", again);
            Assert.Equal(Path.Combine(output, "Monitor Recordings"), Path.GetDirectoryName(screen));
            Assert.Matches(Stamp("Monitor", ""), Path.GetFileName(screen));

            // The Library sees them where the engine writes them: the game's group and "Monitor Recordings".
            File.Delete(again);
            File.Copy(fragmented, game, overwrite: true);
            File.Copy(fragmented, screen, overwrite: true);
            var library = main.Library;
            Until(() => library.Rows.Count == 2 && library.Rows.All(r => r.Length != ""), "both Recordings listed with a length");
            library.GroupBy = Library.LibraryGrouping.Game;
            Assert.Equal(["Alpha", "Monitor Recordings"], library.Entries.OfType<Library.LibraryGroupHeader>().Select(h => h.Name));
            Assert.Contains(engine.State!.Library, i => i.Path == Path.GetRelativePath(output, game));
        }

        // R11: the Recordings a dead engine left open. One torn mid-fragment, one with no whole fragment.
        var boxes = TopLevelBoxes(fragmented);
        var moofs = boxes.Where(b => b.Type == "moof").Select(b => b.Offset).ToList();
        Log?.WriteLine("boxes: " + string.Join(" ", boxes.Select(b => $"{b.Type}@{b.Offset}")));
        // ffmpeg ends with a small audio-only fragment and an mfra; tear the last whole-second fragment instead.
        Assert.True(moofs.Count >= 4, "ffmpeg wrote too few fragments");
        var tornAt = moofs[^2];
        var torn = Path.Combine(output, "Alpha", "Alpha_torn.mp4");
        Truncated(fragmented, torn, tornAt + 200);
        var empty = Path.Combine(output, "Monitor Recordings", "Monitor_empty.mp4");
        Truncated(fragmented, empty, moofs[0]);
        Directory.CreateDirectory(instance.LocalDataFolder);
        File.WriteAllText(
            Path.Combine(instance.LocalDataFolder, "runtime.json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["micMuted"] = true, ["openRecordings"] = new[] { torn, empty } }));

        using (var third = Session.Start(instance, _folder, post, logs))
        {
            var engine = third.Engine;
            Until(() => third.Main.StatusLine.Warnings.Count(w => w.Message.Contains("was cut short", StringComparison.Ordinal)) == 2, "two recording-interrupted warnings on the status line");
            Assert.Equal(2, engine.State!.Warnings.Count(w => w.Code == ErrorCodes.RecordingInterrupted));
            Assert.Contains(engine.State.Warnings, w => w.Code == ErrorCodes.RecordingInterrupted && w.Message.Contains(torn, StringComparison.Ordinal) && w.Message.Contains("the last part that was fully written", StringComparison.Ordinal));
            Assert.Contains(engine.State.Warnings, w => w.Code == ErrorCodes.RecordingInterrupted && w.Message.Contains(empty, StringComparison.Ordinal) && w.Message.Contains("removed", StringComparison.Ordinal));

            Assert.Equal(tornAt, new FileInfo(torn).Length);
            Assert.True(Mp4File.IsComplete(torn));
            Assert.False(File.Exists(empty));
            Assert.DoesNotContain("openRecordings", File.ReadAllText(Path.Combine(instance.LocalDataFolder, "runtime.json")), StringComparison.Ordinal);

            var seconds = Duration(torn);
            Log?.WriteLine($"trimmed: {new FileInfo(torn).Length} bytes, {seconds:0.###} s");
            Assert.InRange(seconds, 2.0, 3.5);
            Assert.Equal("", Decode(torn));
            Until(() => third.Main.Library.Rows.Any(r => r.Path == @"Alpha\Alpha_torn.mp4" && r.Length != ""), "the trimmed Recording listed with a length");
        }

        Log?.WriteLine("engine log:\n" + string.Join("\n", logs.Messages));
        Assert.DoesNotContain(logs.Messages, l => l.StartsWith("Detected", StringComparison.Ordinal)
            || l.StartsWith("Capturing", StringComparison.Ordinal)
            || l.Contains("Recording started", StringComparison.Ordinal)
            || l.StartsWith("Recording to ", StringComparison.Ordinal)
            || l.StartsWith("Monitor Recording to ", StringComparison.Ordinal));
    });

    private static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static Regex Stamp(string name, string suffix) => new($@"^{name}_\d{{4}}-\d\d-\d\d_\d\d-\d\d-\d\d{suffix}\.mp4$");

    private static EffectiveSettings Effective(EngineConnection engine) => SettingsResolver.Resolve(engine.State!.Settings, null);

    private static HotkeyBindingStatus? Status(EngineConnection engine, HotkeyAction action) => engine.State!.Hotkeys.Bindings.SingleOrDefault(b => b.Action == action);

    private static Settings.HotkeyRow Row(Settings.HotkeysPage page, HotkeyAction action) => page.Rows.Single(r => r.Action == action);

    private static string[] Shown(Settings.HotkeysPage page) => [.. page.Rows.Where(r => r.IsShown).Select(r => r.Label)];

    /// <summary>A fragmented MP4 like a Recording's: an empty moov, then 1 s fragments (H.264 and AAC, CPU only).</summary>
    private string Fragmented(string name, int seconds)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "made")).FullName, name);
        var (code, errors) = Exec(
            Tool("ffmpeg.exe"),
            "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", seconds.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "30",
            "-c:a", "aac", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "1000000", path);
        Assert.True(code == 0, "ffmpeg failed: " + errors);
        return path;
    }

    private static double Duration(string path)
    {
        var (code, text) = Exec(Tool("ffprobe.exe"), "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path);
        Assert.True(code == 0, "ffprobe failed: " + text);
        return double.Parse(text.Trim(), CultureInfo.InvariantCulture);
    }

    // Every frame decoded; ffmpeg's error output, empty when it plays cleanly.
    private static string Decode(string path)
    {
        var (code, errors) = Exec(Tool("ffmpeg.exe"), "-v", "error", "-i", path, "-f", "null", "-");
        Assert.True(code == 0, "ffmpeg couldn't decode the file: " + errors);
        return errors.Trim();
    }

    private static (int Code, string Text) Exec(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var errors = stderr.GetAwaiter().GetResult();
        return (process.ExitCode, stdout.Length > 0 ? stdout : errors);
    }

    private static List<(string Type, long Offset)> TopLevelBoxes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var boxes = new List<(string, long)>();
        for (long at = 0; at + 8 <= bytes.Length;)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)at));
            boxes.Add((Encoding.ASCII.GetString(bytes, (int)at + 4, 4), at));
            Assert.True(size >= 8, "an MP4 box of a size this test doesn't read");
            at += size;
        }

        return boxes;
    }

    private static void Truncated(string source, string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(source, path, overwrite: true);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.SetLength(length);
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds.</summary>
    private static void Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, $"Timed out waiting for {what}.");
            Pump(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

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

    /// <summary>The engine with Rewind off, the App's connection, and the main view model.</summary>
    private sealed class Session : IDisposable
    {
        private Session(EngineHost host, EngineConnection engine, MainViewModel main)
        {
            Host = host;
            Engine = engine;
            Main = main;
        }

        public EngineHost Host { get; }

        public EngineConnection Engine { get; }

        public MainViewModel Main { get; }

        public static Session Start(Instance instance, string folder, Action<Action> post, ILoggerFactory logs)
        {
            var host = EngineHost.Start(
                instance,
                logs,
                hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AppLauncher = new StayingLauncher(), Rewind = false });
            var engine = new EngineConnection(new EngineConnectionOptions
            {
                InstanceName = instance.Name,
                EnginePath = Path.Combine(folder, "no-engine.exe"),
                TrustedEnginePath = Environment.ProcessPath!,
            });
            Run(engine.ConnectAsync());
            Assert.Equal(EngineStatus.Connected, engine.Status);
            return new Session(host, engine, new MainViewModel(engine, post));
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
            Run(Host.DisposeAsync().AsTask());
        }
    }

    /// <summary>Keeps every message the engine logs, across its runs.</summary>
    private sealed class ListLoggers : ILoggerFactory
    {
        private readonly Lock _lock = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _messages];
                }
            }
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(ListLoggers owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lock)
                {
                    owner._messages.Add(formatter(state, exception));
                }
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
}
