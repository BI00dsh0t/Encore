using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Bench;

/// <summary>What <c>bench overlay</c> drives in each phase: the dot (and messages) on, off, and whether its window is shown.</summary>
internal interface IOverlayHost : IDisposable
{
    void On();

    void Off();

    bool WindowVisible();
}

/// <summary><c>bench overlay --host app</c>: the real App on a test instance, its Overlay driven by <c>dev.simulate</c>.</summary>
internal sealed class AppOverlayHost : IOverlayHost
{
    public const string ExeName = "Encore.App.exe";
    private const string EngineProcessName = "Encore.Engine";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    // The App's Overlay window: click-through, never activated, a tool window (OverlayWindowStyle).
    private const uint OverlayStyleMask = 0x08000000 | 0x20 | 0x80;

    private readonly Process _app;
    private readonly Instance _instance;
    private readonly EngineClient _client;
    private readonly int _enginePid;
    private readonly bool _messages;
    private readonly Lock _sendLock = new();
    private bool _disposed;

    private AppOverlayHost(Process app, Instance instance, EngineClient client, int enginePid, bool messages)
    {
        _messages = messages;
        _app = app;
        _instance = instance;
        _client = client;
        _enginePid = enginePid;
    }

    /// <summary>Starts the App and its engine; throws when they never answer, after stopping both and deleting their folders.</summary>
    public static AppOverlayHost Start(string exe, string ignoredExe, bool messages, OverlayDotMode? overlayDot = null)
    {
        var instance = Instance.ForTest(Instance.TestPrefix + "bench-overlay-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--tray", "--instance", instance.Name })
        {
            start.ArgumentList.Add(arg);
        }

        var app = Process.Start(start) ?? throw new BenchFailure($"Could not start {exe}.");
        EngineClient? client = null;
        var enginePid = 0;
        try
        {
            (client, enginePid) = Connect(instance, app);
            var id = Canonicalize(ignoredExe);
            var ops = new List<SettingsOp>
            {
                SettingsOp.SetProfile(id, ProfileAttributes.Name, JsonSerializer.SerializeToElement("GPU load")),
                SettingsOp.SetProfile(id, ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(true)),
            };
            if (overlayDot is { } dot)
            {
                ops.Add(SettingsOp.SetGlobal(Keys.OverlayDot.Name, Keys.OverlayDot.ToJson(dot)));
            }

            Wait(client.SendAsync(SettingsMessages.Update, new SettingsUpdate(client.State.Settings.Revision, ops)));
            Console.WriteLine($"App host ready: {instance.Name}, App process {app.Id}, the load's profile Ignored{(overlayDot is { } d ? $", overlayDot {DotName(d)}" : "")}.");
            return new AppOverlayHost(app, instance, client, enginePid, messages);
        }
        catch (Exception e)
        {
            Cleanup(app, instance, client, enginePid);
            if (e is BenchFailure)
            {
                throw;
            }

            throw new BenchFailure($"The App host failed: {e}");
        }
    }

    /// <summary><c>--app-exe</c>; else the newest Release build of the App with the engine beside it.</summary>
    public static string Resolve(BenchContext context)
    {
        if (context.Options.Text("app-exe") is { } given)
        {
            var full = Path.GetFullPath(given);
            return File.Exists(full) ? full : throw new BenchFailure($"The App is not at '{full}'.");
        }

        var release = new DirectoryInfo(Path.Combine(context.RepoRoot, "src", "Encore.App", "bin", "Release"));
        var built = release.Exists
            ? release.GetDirectories()
                .Select(tfm => new FileInfo(Path.Combine(tfm.FullName, ExeName)))
                .Where(f => f.Exists && File.Exists(Path.Combine(f.DirectoryName!, EngineProcessName + ".exe")))
                .OrderBy(f => f.LastWriteTimeUtc)
                .LastOrDefault()
            : null;
        return built?.FullName ?? throw new BenchFailure(
            $"No Release build of the App ({ExeName}). Run dotnet build Encore.slnx -c Release, or pass --app-exe <path>.");
    }

    public void On()
    {
        Simulate(SimulatedEvent.RecordingStarted);
        if (_messages)
        {
            Simulate(SimulatedEvent.ClipSaved);
            Simulate(SimulatedEvent.SaveFailed);
        }
    }

    public void Off() => Simulate(SimulatedEvent.RecordingStopped);

    /// <summary>One Overlay message (a saved notice): <c>bench fps --overlay</c> sends one every 10 s while the dot is on.</summary>
    public void Message() => Simulate(SimulatedEvent.ClipSaved);

    /// <summary>The <c>overlayDot</c> modes as <c>--overlay-dot</c> names them (the setting's own JSON names).</summary>
    public static readonly IReadOnlyDictionary<string, OverlayDotMode> DotModes = new Dictionary<string, OverlayDotMode>(StringComparer.Ordinal)
    {
        ["auto"] = OverlayDotMode.Auto,
        ["always"] = OverlayDotMode.Always,
        ["onChange"] = OverlayDotMode.OnChange,
    };

    public static string DotName(OverlayDotMode mode) => DotModes.First(m => m.Value == mode).Key;

    /// <summary>The <c>--overlay-dot</c> option, or <see cref="OverlayDotMode.Auto"/>; throws for an unknown mode.</summary>
    public static OverlayDotMode ParseDot(BenchOptions options) =>
        options.Text("overlay-dot") is not { } text ? OverlayDotMode.Auto
        : DotModes.TryGetValue(text, out var mode) ? mode
        : throw new BenchUsageException($"--overlay-dot '{text}' is not one of {string.Join(", ", DotModes.Keys)}.");

    /// <summary>Whether the App has a visible Overlay window. It has no handle until it is first shown, so this looks each time.</summary>
    public bool WindowVisible()
    {
        var visible = false;
        var pid = (uint)_app.Id;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == pid
                && (GetWindowLongPtr(hwnd, GwlExStyle) & OverlayStyleMask) == OverlayStyleMask && IsWindowVisible(hwnd))
            {
                visible = true;
                return false;
            }

            return true;
        }, 0);
        return visible;
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
            Cleanup(_app, _instance, _client, _enginePid);
        }
    }

    /// <summary>Serialized: <c>bench fps --overlay</c> sends messages from a timer beside the dot's on and off.</summary>
    private void Simulate(SimulatedEvent simulated)
    {
        lock (_sendLock)
        {
            Wait(_client.SendAsync(DevMessages.Simulate, new DevSimulate(simulated)));
        }
    }

    /// <summary>Connects once the App has started the engine; returns the client and the engine's process id.</summary>
    private static (EngineClient Client, int EnginePid) Connect(Instance instance, Process app)
    {
        var pipe = PipeName.ForCurrentUser(instance.Name);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var pid = 0u;
            var options = new EngineClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                VerifyServer = stream => GetNamedPipeServerProcessId(stream.SafePipeHandle, out pid),
            };
            try
            {
                return (Wait(EngineClient.ConnectAsync(pipe, options)), (int)pid);
            }
            catch (ErrorCodeException) when (deadline.Elapsed < ConnectTimeout && !app.HasExited)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Shuts the engine down (the App quits with it), stops what is left, and deletes the instance's folders.</summary>
    internal static void Cleanup(Process app, Instance instance, EngineClient? client, int enginePid)
    {
        if (client is not null)
        {
            try
            {
                Wait(client.SendAsync(EngineMessages.Shutdown, Empty.Value));
            }
            catch (Exception e) when (e is ErrorCodeException or TimeoutException)
            {
                // It is killed below.
            }

            client.DisposeAsync().AsTask().Wait(ReplyTimeout);
        }

        if (!app.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            Kill(app);
        }

        app.Dispose();

        // The engine outlives the App when its shutdown failed: stop this instance's engine only.
        if (enginePid != 0)
        {
            try
            {
                using var engine = Process.GetProcessById(enginePid);
                if (engine.ProcessName == EngineProcessName)
                {
                    engine.WaitForExit(TimeSpan.FromSeconds(5));
                    Kill(engine);
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        foreach (var folder in new[] { instance.RoamingDataFolder, instance.LocalDataFolder })
        {
            DeleteQuietly(folder);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // It exited in between.
        }
    }

    internal static void DeleteQuietly(string folder)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The engine may hold its log for a moment after it exits.
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Waits for <paramref name="task"/>, rethrowing its own exception (not an <see cref="AggregateException"/>).</summary>
    private static T Wait<T>(Task<T> task) => task.WaitAsync(ReplyTimeout).GetAwaiter().GetResult();

    /// <summary>The Game Profile id as the engine makes it, repeated here because Bench doesn't reference Platform.Windows.</summary>
    internal static string Canonicalize(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"The final path of '{path}' couldn't be read.");
        }

        var final = buffer.ToString();
        final = final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
        return final.ToLowerInvariant();
    }

    private const int GwlExStyle = -20;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrRaw(nint hwnd, int index);

    private static uint GetWindowLongPtr(nint hwnd, int index) => (uint)GetWindowLongPtrRaw(hwnd, index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
