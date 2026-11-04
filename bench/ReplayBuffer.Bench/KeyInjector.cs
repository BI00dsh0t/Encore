using System.Runtime.InteropServices;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Bench;

/// <summary>
/// Presses a key combination with <c>SendInput</c> about once a second, on a thread of its own, for an
/// unattended <c>bench hotkey --inject</c>: the modifiers down, the key down and up, the modifiers up, as one
/// batch. The low-level hook sees each key-down flagged injected. When <c>SendInput</c> inserts nothing
/// (UIPI: a window of higher integrity has the focus, or the desktop is locked) the injector stops and
/// <see cref="Fault"/> names why; <see cref="Faulted"/> fires so a waiting reader stops at once.
/// </summary>
internal sealed class KeyInjector : IDisposable
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;

    private readonly Input[] _batch;
    private readonly TimeSpan _interval;
    private readonly int _maxPresses;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _faulted = new();
    private readonly Thread _thread;
    private int _presses;

    private KeyInjector(KeyCombo combo, TimeSpan interval, int maxPresses)
    {
        _batch = Batch(combo);
        _interval = interval;
        _maxPresses = maxPresses;
        _thread = new Thread(Run) { Name = "Bench key injector", IsBackground = true };
    }

    /// <summary>Why the injector stopped early, or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>Cancelled when <see cref="Fault"/> is set.</summary>
    public CancellationToken Faulted => _faulted.Token;

    /// <summary>Presses sent so far.</summary>
    public int Presses => Volatile.Read(ref _presses);

    /// <summary>Starts pressing after one <paramref name="interval"/>, at most <paramref name="maxPresses"/> times.</summary>
    public static KeyInjector Start(KeyCombo combo, TimeSpan interval, int maxPresses)
    {
        var injector = new KeyInjector(combo, interval, maxPresses);
        injector._thread.Start();
        return injector;
    }

    /// <summary>The <c>SendInput</c> events for one press of <paramref name="combo"/>.</summary>
    internal static Input[] Batch(KeyCombo combo)
    {
        var modifiers = new List<ushort>();
        if ((combo.Modifiers & HotkeyModifiers.Control) != 0) modifiers.Add(VkControl);
        if ((combo.Modifiers & HotkeyModifiers.Alt) != 0) modifiers.Add(VkMenu);
        if ((combo.Modifiers & HotkeyModifiers.Shift) != 0) modifiers.Add(VkShift);
        if ((combo.Modifiers & HotkeyModifiers.Win) != 0) modifiers.Add(VkLWin);

        var batch = new List<Input>();
        batch.AddRange(modifiers.Select(vk => Key(vk, up: false)));
        batch.Add(Key((ushort)combo.Vk, up: false));
        batch.Add(Key((ushort)combo.Vk, up: true));
        batch.AddRange(Enumerable.Reverse(modifiers).Select(vk => Key(vk, up: true)));
        return [.. batch];
    }

    /// <summary>One press of <paramref name="combo"/> now, on this thread; null when it went in, else why it didn't.</summary>
    public static string? PressOnce(KeyCombo combo)
    {
        var batch = Batch(combo);
        var sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<Input>());
        if (sent == batch.Length)
        {
            return null;
        }

        var error = Marshal.GetLastPInvokeError();
        if (sent > 0)
        {
            var lift = batch.Where(i => (i.Keyboard.Flags & KeyEventKeyUp) != 0).ToArray();
            _ = SendInput((uint)lift.Length, lift, Marshal.SizeOf<Input>());
        }

        return $"SendInput inserted {sent} of {batch.Length} key events (error {error}): input is blocked, "
            + "usually by UIPI (a window of higher integrity has the focus) or a locked or secure desktop";
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
        _faulted.Dispose();
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardInput
        {
            Vk = vk,
            Scan = (ushort)MapVirtualKey(vk, 0),
            Flags = up ? KeyEventKeyUp : 0,
        },
    };

    private void Run()
    {
        while (_presses < _maxPresses && !_stop.Token.WaitHandle.WaitOne(_interval))
        {
            var sent = SendInput((uint)_batch.Length, _batch, Marshal.SizeOf<Input>());
            if (sent != _batch.Length)
            {
                var error = Marshal.GetLastPInvokeError();
                if (sent > 0)
                {
                    // Half a press: lift whatever went down, so no modifier stays held.
                    var lift = _batch.Where(i => (i.Keyboard.Flags & KeyEventKeyUp) != 0).ToArray();
                    _ = SendInput((uint)lift.Length, lift, Marshal.SizeOf<Input>());
                }

                Fault = $"SendInput inserted {sent} of {_batch.Length} key events (error {error}): input is blocked, "
                    + "usually by UIPI (a window of higher integrity has the focus) or a locked or secure desktop";
                _faulted.Cancel();
                return;
            }

            Interlocked.Increment(ref _presses);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary><c>INPUT</c>: the type, then the union at offset 8, sized by its largest member (<c>MOUSEINPUT</c>).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
