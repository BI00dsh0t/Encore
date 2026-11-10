using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Encore.Contracts.Settings;

namespace Encore.Bench;

/// <summary>A key and its modifiers, parsed from text such as <c>ctrl+alt+f9</c>.</summary>
internal sealed record KeyCombo(int Vk, int Modifiers, string Text)
{
    /// <summary>
    /// Modifiers <c>ctrl</c>/<c>control</c>, <c>alt</c>, <c>shift</c> and <c>win</c>, then one key: <c>f1</c>–<c>f24</c>,
    /// a letter, a digit, or <c>0x</c> and a virtual-key code in hex.
    /// </summary>
    /// <exception cref="BenchUsageException">Not a key combination.</exception>
    public static KeyCombo Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            throw new BenchUsageException($"--key '{text}' is not a key combination such as ctrl+alt+f9.");
        }

        var modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Win,
                _ => throw new BenchUsageException($"--key '{text}': '{part}' is not ctrl, alt, shift or win."),
            };
            if ((modifiers & flag) != 0)
            {
                throw new BenchUsageException($"--key '{text}' names '{part}' twice.");
            }

            modifiers |= flag;
        }

        var key = parts[^1].ToLowerInvariant();
        int vk;
        if (key.Length >= 2 && key[0] == 'f' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            vk = 0x70 + n - 1;
        }
        else if (key.Length == 1 && char.IsAsciiLetter(key[0]))
        {
            vk = char.ToUpperInvariant(key[0]);
        }
        else if (key.Length == 1 && char.IsAsciiDigit(key[0]))
        {
            vk = key[0];
        }
        else if (key.StartsWith("0x", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) && code is > 0 and < 0xFF)
        {
            vk = code;
        }
        else
        {
            throw new BenchUsageException($"--key '{text}': '{parts[^1]}' is not f1–f24, a letter, a digit or 0x<vk>.");
        }

        return new KeyCombo(vk, modifiers, text);
    }
}

/// <summary>
/// A <c>WH_KEYBOARD_LL</c> hook on a thread of its own that stamps QPC on each key-down of one virtual key.
/// A low-level hook runs before the system's Hotkey handling, so a stamp comes before the engine's
/// <c>WM_HOTKEY</c> for the same press.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmQuit = 0x0012;
    private const int LlkhfInjected = 0x10;

    private readonly int _vk;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentQueue<(long Qpc, bool Injected)> _downs = new();
    private Native.HookProc? _proc;
    private nint _hook;
    private uint _threadId;
    private int _error;

    public KeyboardHook(int vk)
    {
        _vk = vk;
        _thread = new Thread(Run) { Name = "Bench keyboard hook", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
        if (_hook == 0)
        {
            throw new BenchFailure($"SetWindowsHookEx(WH_KEYBOARD_LL) failed with error {_error}.");
        }
    }

    /// <summary>Takes the key-downs stamped since the last call, oldest first.</summary>
    public List<(long Qpc, bool Injected)> Take()
    {
        var taken = new List<(long, bool)>();
        while (_downs.TryDequeue(out var down))
        {
            taken.Add(down);
        }

        return taken;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            _ = Native.PostThreadMessage(_threadId, WmQuit, 0, 0);
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(WhKeyboardLl, _proc, Native.GetModuleHandle(null), 0);
        _error = Marshal.GetLastWin32Error();
        _ready.Set();
        if (_hook == 0)
        {
            return;
        }

        try
        {
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = Native.TranslateMessage(ref message);
                _ = Native.DispatchMessage(ref message);
            }
        }
        finally
        {
            _ = Native.UnhookWindowsHookEx(_hook);
        }
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var qpc = Stopwatch.GetTimestamp();
            var data = Marshal.PtrToStructure<Native.KbdLlHookStruct>(lParam);
            if (data.VkCode == _vk)
            {
                _downs.Enqueue((qpc, (data.Flags & LlkhfInjected) != 0));
            }
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    private static class Native
    {
        public delegate nint HookProc(int code, nint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, HookProc proc, nint module, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandle(string? name);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern int GetMessage(out Msg message, nint hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessage(ref Msg message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint threadId, int message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public nint Hwnd;
            public uint Message;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }
    }
}
