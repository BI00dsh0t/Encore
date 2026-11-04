using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// Runs <see cref="HotkeyServeTests"/> alone: the press latency includes <c>SendInput</c>, which the
/// encode tests running beside it would slow.
/// </summary>
[CollectionDefinition(nameof(HotkeyServeCollection), DisableParallelization = true)]
public sealed class HotkeyServeCollection;

/// <summary>
/// Runs the built engine's <c>serve</c> on a throwaway test instance with Hotkeys bound, and checks
/// registration and presses from outside, with <c>RegisterHotKey</c> and <c>SendInput</c> of its own.
/// Each test uses its own Ctrl+Alt+Shift+F-key combinations, so an engine a failed test left running can't hold another test's.
/// </summary>
[Collection(nameof(HotkeyServeCollection))]
public sealed class HotkeyServeTests : IDisposable
{
    private const int CtrlAltShift = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
    private const int VkF18 = 0x81;
    private const int VkF19 = 0x82;
    private const int VkF20 = 0x83;
    private const int VkF21 = 0x84;
    private const int VkF23 = 0x86;
    private const int VkF24 = 0x87;

    /// <summary>The longest a press may take from <c>SendInput</c> to its stamp; anything under it goes unnoticed.</summary>
    private const double MaxPressMilliseconds = 1000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _instance = ServeCommand.TestInstancePrefix + Guid.NewGuid().ToString("N");
    private readonly List<Process> _processes = [];
    private readonly ConcurrentQueue<string> _stdout = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EngineExe => Path.Combine(EngineBinaries.Folder, "ReplayBuffer.Engine.exe");

    private string RoamingFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _instance);

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }

            process.Dispose();
        }

        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var folder = Path.Combine(Environment.GetFolderPath(root), _instance);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <remarks>
    /// Opt-in: <c>SendInput</c> goes through the desktop's real keyboard, so a window that takes every
    /// key while it has focus (a Remote Desktop client, msrdc) swallows all 20 presses and the test fails.
    /// Run it with that window out of focus.
    /// </remarks>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AnInjectedPressArrivesPromptlyAndIsLogged()
    {
        var save = new HotkeyBinding(VkF24, CtrlAltShift);
        WriteHotkeys(new HotkeySettings(save, null, null, null, null, null));
        var engine = await StartServeAsync();
        var presses = Channel.CreateUnbounded<HotkeyPress>();
        await using var client = await ConnectAsync(e =>
        {
            if (e.Type == HotkeyMessages.Pressed.Name)
            {
                presses.Writer.TryWrite(HotkeyMessages.Pressed.Read(e.Payload));
            }
        });
        Assert.Equal(new HotkeyBindingStatus(HotkeyAction.Save, save, true, null), client.State.Hotkeys.Bindings[0]);

        // Every press must arrive, stamped promptly: the builder's bar is no press over a second.
        // Milliseconds don't matter. (qpc - before) includes SendInput, which runs every other
        // process's low-level keyboard hook (1–2.5 ms seen with a Remote Desktop client running),
        // so the timings are logged, not held to a millisecond.
        var latencies = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var (before, _) = Input.Press(VkF24, CtrlAltShift);
            var press = await presses.Reader.ReadAsync(Token).AsTask().WaitAsync(Wait, Token);

            Assert.Equal(HotkeyAction.Save, press.Action);
            latencies.Add(Milliseconds(press.Qpc - before));
            await Task.Delay(50, Token);
        }

        TestContext.Current.TestOutputHelper?.WriteLine("press latencies, ms: " + string.Join(", ", latencies.Select(ms => ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.All(latencies, ms => Assert.InRange(ms, 0, MaxPressMilliseconds));
        Assert.Equal(HotkeyAction.Save, client.State.LastHotkeyPress?.Action);

        await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);
        await engine.WaitForExitAsync(Token).WaitAsync(Wait, Token);
        Assert.Contains(_stdout, line => line.Contains("Hotkey save pressed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACombinationAnotherProcessHoldsIsTakenAndTheOthersRegister()
    {
        using var held = new HeldHotkey(VkF23, CtrlAltShift);
        Assert.True(held.Registered);
        var save = new HotkeyBinding(VkF23, CtrlAltShift);
        var micMute = new HotkeyBinding(VkF20, CtrlAltShift);
        WriteHotkeys(new HotkeySettings(save, micMute, null, null, null, null));

        await StartServeAsync();
        await using var client = await ConnectAsync();

        var bindings = client.State.Hotkeys.Bindings;
        Assert.Equal(new HotkeyBindingStatus(HotkeyAction.Save, save, false, ErrorCodes.HotkeyTaken), bindings[0]);
        Assert.Equal(new HotkeyBindingStatus(HotkeyAction.MicMute, micMute, true, null), bindings[1]);
    }

    [Fact]
    public async Task ARebindFreesTheOldCombinationAndADuplicateUpdateIsRefused()
    {
        var old = new HotkeyBinding(VkF21, CtrlAltShift);
        var next = new HotkeyBinding(VkF19, CtrlAltShift);
        WriteHotkeys(new HotkeySettings(old, null, null, null, null, null));
        await StartServeAsync();
        var statuses = Channel.CreateUnbounded<HotkeyStatus>();
        await using var client = await ConnectAsync(e =>
        {
            if (e.Type == HotkeyMessages.Status.Name)
            {
                statuses.Writer.TryWrite(HotkeyMessages.Status.Read(e.Payload));
            }
        });
        Assert.True(client.State.Hotkeys.Bindings[0].Registered);
        Assert.False(HeldHotkey.CanRegister(VkF21, CtrlAltShift));

        var duplicate = new HotkeySettings(next, next, null, null, null, null);
        var refused = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(SettingsMessages.Update, SetHotkeys(0, duplicate), Token));
        Assert.Equal(ErrorCodes.HotkeyDuplicate, refused.Code);

        await client.SendAsync(SettingsMessages.Update, SetHotkeys(0, new HotkeySettings(next, null, null, null, null, null)), Token);
        var status = await statuses.Reader.ReadAsync(Token).AsTask().WaitAsync(Wait, Token);

        Assert.Equal(new HotkeyBindingStatus(HotkeyAction.Save, next, true, null), status.Bindings[0]);
        Assert.True(HeldHotkey.CanRegister(VkF21, CtrlAltShift));
        Assert.False(HeldHotkey.CanRegister(VkF19, CtrlAltShift));
    }

    [Fact]
    public async Task AHandEditedDuplicateRegistersTheFirstActionOnly()
    {
        var shared = new HotkeyBinding(VkF18, CtrlAltShift);
        WriteHotkeys(new HotkeySettings(shared, shared, null, null, null, null));

        await StartServeAsync();
        await using var client = await ConnectAsync();

        var bindings = client.State.Hotkeys.Bindings;
        Assert.Equal(new HotkeyBindingStatus(HotkeyAction.Save, shared, true, null), bindings[0]);
        Assert.Equal(new HotkeyBindingStatus(HotkeyAction.MicMute, shared, false, ErrorCodes.HotkeyDuplicate), bindings[1]);
    }

    [Fact]
    public async Task ATestInstanceWithNoHotkeysRegistersNothing()
    {
        await StartServeAsync();
        await using var client = await ConnectAsync();

        Assert.Equal(HotkeyStatus.Unbound, client.State.Hotkeys);
    }

    private static double Milliseconds(long qpcTicks) => qpcTicks * 1000.0 / Stopwatch.Frequency;

    private static SettingsUpdate SetHotkeys(long revision, HotkeySettings hotkeys) =>
        new(revision, [SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(hotkeys))]);

    private void WriteHotkeys(HotkeySettings hotkeys)
    {
        var document = SettingsDocument.Empty with { Global = SettingsMaps.EmptyValues.Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(hotkeys)) };
        Directory.CreateDirectory(RoamingFolder);
        File.WriteAllText(Path.Combine(RoamingFolder, "settings.json"), JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument));
    }

    private Task<EngineClient> ConnectAsync(Action<IpcEvent>? onEvent = null) =>
        EngineClient.ConnectAsync(PipeName.ForCurrentUser(_instance), new EngineClientOptions { OnEvent = onEvent }, Token);

    private async Task<Process> StartServeAsync()
    {
        var start = new ProcessStartInfo(EngineExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--instance");
        start.ArgumentList.Add(_instance);
        var process = Process.Start(start)!;
        _processes.Add(process);

        string? line;
        do
        {
            line = await process.StandardOutput.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);
            if (line is not null)
            {
                _stdout.Enqueue(line);
            }
        }
        while (line is not null && !line.StartsWith("listening ", StringComparison.Ordinal));

        Assert.Equal("listening " + PipeName.ForCurrentUser(_instance), line);

        // Keep reading, so the engine never blocks on a full pipe and the log lines are kept.
        _ = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } next)
            {
                _stdout.Enqueue(next);
            }
        });
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return process;
    }

    /// <summary>A Hotkey this test process holds, registered on a thread of its own until disposed.</summary>
    private sealed class HeldHotkey : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public HeldHotkey(int vk, int modifiers)
        {
            using var registered = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                Registered = Native.RegisterHotKey(0, 1, (uint)(modifiers | HotkeyModifiers.NoRepeat), (uint)vk);
                registered.Set();
                _release.Wait();
                if (Registered)
                {
                    Native.UnregisterHotKey(0, 1);
                }
            })
            { IsBackground = true };
            _thread.Start();
            registered.Wait();
        }

        public bool Registered { get; private set; }

        /// <summary>Whether the combination is free now: registers it and lets it go.</summary>
        public static bool CanRegister(int vk, int modifiers)
        {
            using var held = new HeldHotkey(vk, modifiers);
            return held.Registered;
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }
    }

    internal static class Input
    {
        private const uint InputKeyboard = 1;
        private const uint KeyUp = 0x0002;
        private const ushort VkShift = 0x10;
        private const ushort VkControl = 0x11;
        private const ushort VkMenu = 0x12;

        /// <summary>
        /// Injects the modifiers down, then the key down on its own, then the key and modifiers up.
        /// Each injected event passes through every low-level hook on the desktop, so the modifiers go
        /// first and the clock starts just before the key down.
        /// </summary>
        /// <returns>The QPC read just before and just after the <c>SendInput</c> of the key down.</returns>
        public static (long Before, long After) Press(int vk, int modifiers)
        {
            var mods = new List<ushort>();
            if ((modifiers & HotkeyModifiers.Control) != 0)
            {
                mods.Add(VkControl);
            }

            if ((modifiers & HotkeyModifiers.Alt) != 0)
            {
                mods.Add(VkMenu);
            }

            if ((modifiers & HotkeyModifiers.Shift) != 0)
            {
                mods.Add(VkShift);
            }

            Send([.. mods.Select(m => Key(m, up: false))]);
            try
            {
                var before = Stopwatch.GetTimestamp();
                Send([Key((ushort)vk, up: false)]);
                return (before, Stopwatch.GetTimestamp());
            }
            finally
            {
                Send([Key((ushort)vk, up: true), .. Enumerable.Reverse(mods).Select(m => Key(m, up: true))]);
            }
        }

        private static void Send(Native.Input[] inputs)
        {
            var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
            Assert.Equal((uint)inputs.Length, sent);
        }

        private static Native.Input Key(ushort vk, bool up) => new()
        {
            Type = InputKeyboard,
            Keyboard = new Native.KeyboardInput { Vk = vk, Flags = up ? KeyUp : 0 },
        };
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(nint hwnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, [In] Input[] inputs, int size);

        /// <summary><c>INPUT</c> on x64: the type, then the union at offset 8, 40 bytes in all.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct Input
        {
            [FieldOffset(0)]
            public uint Type;

            [FieldOffset(8)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardInput
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }
    }
}
