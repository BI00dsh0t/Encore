using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Bench;

/// <summary>The real App and its engine on a throwaway test instance, driven by injected Hotkeys; never the installed App.</summary>
internal sealed class LiveApp : IDisposable
{
    public const string PublishedFolder = "publish/Encore";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _app;
    private readonly EngineClient _client;
    private readonly int _enginePid;
    private readonly Channel<IpcEvent> _clips;
    private readonly Lock _sendLock = new();
    private bool _disposed;

    private LiveApp(Process app, Instance instance, EngineClient client, int enginePid, Channel<IpcEvent> clips, KeyCombo save, KeyCombo record)
    {
        _app = app;
        Instance = instance;
        _client = client;
        _enginePid = enginePid;
        _clips = clips;
        SaveKey = save;
        RecordKey = record;
    }

    public Instance Instance { get; }

    public KeyCombo SaveKey { get; }

    public KeyCombo RecordKey { get; }

    public EngineClient Client => _client;

    public int AppPid => _app.Id;

    public int EnginePid => _enginePid;

    /// <summary>The App's path: the option, else the local publish when the engine is beside it, else the newest Release build.</summary>
    public static string Resolve(BenchContext context)
    {
        if (context.Options.Text("app-exe") is not null)
        {
            return AppOverlayHost.Resolve(context);
        }

        var published = Path.Combine(context.RepoRoot, "publish", "Encore", AppOverlayHost.ExeName);
        return File.Exists(published) && File.Exists(Path.Combine(Path.GetDirectoryName(published)!, "Encore.Engine.exe"))
            ? published
            : AppOverlayHost.Resolve(context);
    }

    /// <summary>Starts the App on a new test instance and applies the settings and Hotkeys, with Feedback Sounds off.</summary>
    public static LiveApp Start(string appExe, string name, KeyCombo save, KeyCombo record, IReadOnlyList<SettingsOp> ops)
    {
        var instance = Instance.ForTest(Instance.TestPrefix + name + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        var start = new ProcessStartInfo(appExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--tray", "--instance", instance.Name, "--rewind" })
        {
            start.ArgumentList.Add(arg);
        }

        Console.WriteLine($"App: {appExe} --tray --instance {instance.Name} --rewind");
        var app = Process.Start(start) ?? throw new BenchFailure($"Could not start {appExe}.");
        EngineClient? client = null;
        var enginePid = 0;
        var clips = Channel.CreateUnbounded<IpcEvent>();
        try
        {
            (client, enginePid) = Connect(instance, app, clips);
            var all = new List<SettingsOp>(ops)
            {
                SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(
                    new HotkeyBinding(save.Vk, save.Modifiers), null, new HotkeyBinding(record.Vk, record.Modifiers), null, null, null))),
                SettingsOp.SetGlobal(Keys.RecordHotkeyMode.Name, Keys.RecordHotkeyMode.ToJson(RecordHotkeyMode.Toggle)),
                SettingsOp.SetGlobal(Keys.FeedbackSounds.Name, Keys.FeedbackSounds.ToJson(new FeedbackSounds(false, false, false, false, false))),
                SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),

                // A synthetic load is found only by its fullscreen window; one added by hand keeps its own choice.
                SettingsOp.SetGlobal(Keys.CaptureFullscreenApps.Name, Keys.CaptureFullscreenApps.ToJson(true)),
            };
            var live = new LiveApp(app, instance, client, enginePid, clips, save, record);
            live.Update([.. all]);
            foreach (var action in new[] { HotkeyAction.Save, HotkeyAction.RecordToggle })
            {
                live.Until(c => c.State.Hotkeys.Bindings.FirstOrDefault(b => b.Action == action) is { Code: not null } or { Registered: true },
                    ReplyTimeout, $"the {action} Hotkey's status");
                var status = client.State.Hotkeys.Bindings.First(b => b.Action == action);
                if (!status.Registered)
                {
                    throw new BenchFailure($"{status.Code}: the engine did not register the {action} Hotkey; pick another key.");
                }
            }

            Console.WriteLine($"App {app.Id} and engine {enginePid} up on {instance.Name}; Save {save.Text}, Record {record.Text}.");
            return live;
        }
        catch (Exception e)
        {
            AppOverlayHost.Cleanup(app, instance, client, enginePid);
            if (e is BenchFailure)
            {
                throw;
            }

            throw new BenchFailure($"The App failed to start: {e.Message}");
        }
    }

    /// <summary>Lists a program as a game, named, with Recordings on and its Ignored flag set as given; returns its profile id.</summary>
    public string AddGame(string exe, string name, bool ignored)
    {
        Wait(_client.SendAsync(GameMessages.Add, new GameAdd(exe)));
        var id = AppOverlayHost.Canonicalize(exe);
        Update(
            SettingsOp.SetProfile(id, ProfileAttributes.Name, JsonSerializer.SerializeToElement(name)),
            SettingsOp.SetProfile(id, ProfileAttributes.RecordingsEnabled, JsonSerializer.SerializeToElement(true)),
            SettingsOp.SetProfile(id, ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(ignored)));
        return id;
    }

    public void SetIgnored(string profile, bool ignored) =>
        Update(SettingsOp.SetProfile(profile, ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(ignored)));

    /// <summary>Waits for Rewind to buffer on <paramref name="profile"/>.</summary>
    public void WaitBuffering(string profile, TimeSpan timeout, Func<string?>? abort = null) =>
        Until(c => c.State.Capture.Replay == ReplayState.Buffering && c.State.TargetGame?.ProfileId == profile, timeout,
            "Rewind buffering on the game", abort);

    /// <summary>Presses Record and waits for the Recording to run; returns the QPC just before the press and the file.</summary>
    public (long PressQpc, string Path) StartRecording(TimeSpan timeout)
    {
        // A session that has just started can't record yet, so the press is repeated every 2 s until the Recording runs.
        var before = _client.State.LastRecordingStarted;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var qpc = Stopwatch.GetTimestamp();
            Press(RecordKey);
            try
            {
                Until(c => c.State.Capture.Recording == RecordingState.Running && !ReferenceEquals(c.State.LastRecordingStarted, before) && c.State.LastRecordingStarted is not null,
                    TimeSpan.FromSeconds(2), "the Recording to start");
                return (qpc, _client.State.LastRecordingStarted!.Path);
            }
            catch (BenchFailure) when (clock.Elapsed < timeout && _client.State.Capture.Recording != RecordingState.Running)
            {
                // Not started: press again.
            }
        }
    }

    /// <summary>Presses Record again and waits for the Recording to stop; returns its stop.</summary>
    public RecordingStopped StopRecording(TimeSpan timeout)
    {
        var before = _client.State.LastRecordingStopped;
        Press(RecordKey);
        Until(c => c.State.Capture.Recording == RecordingState.Off && c.State.LastRecordingStopped is { } s && !ReferenceEquals(s, before),
            timeout, "the Recording to stop");
        return _client.State.LastRecordingStopped!;
    }

    /// <summary>Presses Save and waits for the saved or failed event; returns the Clip's path, or why there is none.</summary>
    public (string? Path, string? Failure) Save(TimeSpan timeout)
    {
        while (_clips.Reader.TryRead(out _))
        {
            // A late event from an earlier press.
        }

        Press(SaveKey);
        try
        {
            using var wait = new CancellationTokenSource(timeout);
            var e = _clips.Reader.ReadAsync(wait.Token).AsTask().GetAwaiter().GetResult();
            return e.Type == CaptureMessages.Saved.Name
                ? (CaptureMessages.Saved.Read(e.Payload).Path, null)
                : (null, "clip.failed: " + CaptureMessages.Failed.Read(e.Payload).Reason);
        }
        catch (OperationCanceledException)
        {
            return (null, $"no clip.saved or clip.failed within {timeout.TotalSeconds:0} s");
        }
    }

    /// <summary>One Overlay message (a saved notice through <c>dev.simulate</c>), as <c>bench fps --overlay</c> sends.</summary>
    public void Message() => Send(() => Wait(_client.SendAsync(DevMessages.Simulate, new DevSimulate(SimulatedEvent.ClipSaved))));

    /// <summary>Applies the ops; a conflict, as when the App changes a setting while starting, is retried on the newer revision.</summary>
    public void Update(params SettingsOp[] ops) => Send(() =>
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = Wait(_client.SendAsync(SettingsMessages.Update, new SettingsUpdate(_client.State.Settings.Revision, ops)));
                Until(c => c.State.Settings.Revision >= result.Revision, ReplyTimeout, "settings.changed");
                return;
            }
            catch (ErrorCodeException e) when (e.Code == ErrorCodes.SettingsConflict && attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    });

    public void Until(Func<EngineClient, bool> condition, TimeSpan timeout, string what, Func<string?>? abort = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition(_client))
        {
            if (_app.HasExited)
            {
                throw new BenchFailure($"Waiting for {what}: the App exited ({_app.ExitCode}).");
            }

            if (!_client.IsConnected)
            {
                throw new BenchFailure($"Waiting for {what}: the engine's pipe closed.");
            }

            if (abort?.Invoke() is { } why)
            {
                throw new BenchFailure($"Waiting for {what}: {why}.");
            }

            if (clock.Elapsed > timeout)
            {
                throw new BenchFailure($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>A copy of the instance's logs (the engine's and the App's), before the instance is deleted.</summary>
    public void CopyLogs(string target)
    {
        var logs = Path.Combine(Instance.LocalDataFolder, "logs");
        if (!Directory.Exists(logs))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(logs))
        {
            try
            {
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = File.Create(Path.Combine(target, Path.GetFileName(file)));
                source.CopyTo(copy);
            }
            catch (IOException)
            {
                // A log the engine still holds exclusively: skipped.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sendLock)
        {
            AppOverlayHost.Cleanup(_app, Instance, _client, _enginePid);
        }
    }

    private static void Press(KeyCombo combo)
    {
        if (KeyInjector.PressOnce(combo) is { } fault)
        {
            throw new BenchFailure($"Could not press {combo.Text}: {fault}");
        }
    }

    private void Send(Action send)
    {
        lock (_sendLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            send();
        }
    }

    private static (EngineClient Client, int EnginePid) Connect(Instance instance, Process app, Channel<IpcEvent> clips)
    {
        var pipe = PipeName.ForCurrentUser(instance.Name);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var pid = 0u;
            var options = new EngineClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                VerifyServer = stream => AppOverlayHost.GetNamedPipeServerProcessId(stream.SafePipeHandle, out pid),
                OnEvent = e =>
                {
                    if (e.Type == CaptureMessages.Saved.Name || e.Type == CaptureMessages.Failed.Name)
                    {
                        clips.Writer.TryWrite(e);
                    }
                },
            };
            try
            {
                return (Wait(EngineClient.ConnectAsync(pipe, options)), (int)pid);
            }
            catch (Exception e) when (e is ErrorCodeException or TimeoutException && deadline.Elapsed < StartTimeout && !app.HasExited)
            {
                Thread.Sleep(500);
            }
            catch (Win32Exception) when (deadline.Elapsed < StartTimeout && !app.HasExited)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static T Wait<T>(Task<T> task) => task.WaitAsync(ReplyTimeout).GetAwaiter().GetResult();
}

/// <summary><c>bench fps --overlay --recording</c>: the real App with the load as its game, recording during the capture runs.</summary>
internal sealed class LiveCapture : IDisposable
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);

    private readonly LiveApp _app;
    private readonly string _profile;

    private LiveCapture(LiveApp app, string profile)
    {
        _app = app;
        _profile = profile;
    }

    public static LiveCapture Start(string appExe, string loadExe, CaptureSettings settings, OverlayDotMode dot)
    {
        var clips = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Encore-Bench-fps-" + Environment.ProcessId)).FullName;
        var app = LiveApp.Start(appExe, "fps", KeyCombo.Parse("ctrl+alt+shift+f16"), KeyCombo.Parse("ctrl+alt+shift+f17"),
        [
            SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(clips)),
            SettingsOp.SetGlobal(Keys.OverlayDot.Name, Keys.OverlayDot.ToJson(dot)),
            SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, settings.Width, settings.Height, settings.Fps, settings.BitrateKbps, RateControl.Cbr))),
        ]);
        try
        {
            var profile = app.AddGame(loadExe, "GPU load", ignored: true);
            return new LiveCapture(app, profile) { Clips = clips };
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    /// <summary>The Output Folder the Recordings go to: deleted on dispose.</summary>
    public string Clips { get; private init; } = "";

    public void On()
    {
        _app.SetIgnored(_profile, false);
        _app.WaitBuffering(_profile, StateTimeout);
        _app.StartRecording(StateTimeout);
    }

    public void Off()
    {
        if (_app.Client.State.Capture.Recording == RecordingState.Running)
        {
            _app.StopRecording(StateTimeout);
        }

        _app.SetIgnored(_profile, true);
        _app.Until(c => c.State.Capture.Replay == ReplayState.Off, StateTimeout, "Rewind to stop");
    }

    public void Message() => _app.Message();

    /// <summary>Where the instance's logs are copied before it is deleted; null keeps none.</summary>
    public string? LogsTo { get; set; }

    public void Dispose()
    {
        if (LogsTo is not null)
        {
            _app.CopyLogs(LogsTo);
        }

        _app.Dispose();
        AppOverlayHost.DeleteQuietly(Clips);
    }
}
