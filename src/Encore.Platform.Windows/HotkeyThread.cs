using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Encore.Contracts.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Windows;

/// <summary>How registering one Hotkey went.</summary>
public enum HotkeyRegistrationOutcome
{
    Registered,

    /// <summary>Another process or thread holds the combination: <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>.</summary>
    Taken,

    Failed,
}

/// <summary>The result for one binding; Error is the Win32 error, or 0 when registered.</summary>
public sealed record HotkeyRegistrationResult(HotkeyAction Action, HotkeyRegistrationOutcome Outcome, int Error);

/// <summary>The engine's Hotkey thread: registers Hotkeys with RegisterHotKey, never a hook, and timestamps each press at once.</summary>
public sealed unsafe class HotkeyThread : IDisposable
{
    private const uint WmApply = PInvoke.WM_APP;
    private const uint WmStop = PInvoke.WM_APP + 1;

    private readonly Action<HotkeyAction, long> _pressed;
    private readonly ConcurrentQueue<ApplyRequest> _requests = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    // Hotkey thread only: the actions registered now.
    private readonly List<HotkeyAction> _registered = [];

    private Exception? _startFailure;
    private HWND _window;
    private int _disposed;

    /// <summary>The pressed callback runs on the Hotkey thread with the press's QPC time; it must return quickly.</summary>
    public HotkeyThread(Action<HotkeyAction, long> pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        _pressed = pressed;
        _thread = new Thread(Run) { Name = "Hotkeys", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
        if (_startFailure is not null)
        {
            _thread.Join();
            _ready.Dispose();
            throw new InvalidOperationException("The Hotkey window couldn't be created.", _startFailure);
        }
    }

    // HWND_MESSAGE: the parent that makes a message-only window.
    private static HWND MessageOnlyParent => new((void*)-3);

    /// <summary>Replaces every registered Hotkey, on the Hotkey thread, and blocks until done; one result per binding, in order.</summary>
    public IReadOnlyList<HotkeyRegistrationResult> Apply(IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var request = new ApplyRequest([.. bindings]);
        _requests.Enqueue(request);
        if (!PInvoke.PostMessage(_window, WmApply, default, default))
        {
            throw new ObjectDisposedException(nameof(HotkeyThread), "The Hotkey thread isn't taking messages.");
        }

        request.Done.Wait();
        return request.Results ?? throw new ObjectDisposedException(nameof(HotkeyThread), "The Hotkey thread stopped before applying.");
    }

    /// <summary>Unregisters every Hotkey, destroys the window and ends the thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (PInvoke.PostMessage(_window, WmStop, default, default) && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }

        _ready.Dispose();
    }

    // Ids start at 1; WM_HOTKEY's wParam carries the id back.
    private static int Id(HotkeyAction action) => (int)action + 1;

    private void Run()
    {
        try
        {
            fixed (char* className = "Message")
            {
                _window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageOnlyParent, HMENU.Null, HINSTANCE.Null, null);
            }

            if (_window.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        catch (Exception e)
        {
            _startFailure = e;
            _ready.Set();
            return;
        }

        _ready.Set();
        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) > 0)
        {
            if (msg.message == PInvoke.WM_HOTKEY)
            {
                // The press's timestamp is read before anything else runs.
                long qpc;
                PInvoke.QueryPerformanceCounter(&qpc);
                _pressed((HotkeyAction)((int)msg.wParam.Value - 1), qpc);
            }
            else if (msg.message == WmApply)
            {
                while (_requests.TryDequeue(out var request))
                {
                    request.Results = Register(request.Bindings);
                    request.Done.Set();
                }
            }
            else if (msg.message == WmStop)
            {
                break;
            }
            else
            {
                PInvoke.DispatchMessage(&msg);
            }
        }

        UnregisterAll();
        PInvoke.DestroyWindow(_window);
        while (_requests.TryDequeue(out var request))
        {
            request.Done.Set();
        }
    }

    private List<HotkeyRegistrationResult> Register(IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> bindings)
    {
        UnregisterAll();
        var results = new List<HotkeyRegistrationResult>(bindings.Count);
        foreach (var (action, binding) in bindings)
        {
            var modifiers = (HOT_KEY_MODIFIERS)(binding.Modifiers | HotkeyModifiers.NoRepeat);
            if (PInvoke.RegisterHotKey(_window, Id(action), modifiers, (uint)binding.Vk))
            {
                _registered.Add(action);
                results.Add(new HotkeyRegistrationResult(action, HotkeyRegistrationOutcome.Registered, 0));
                continue;
            }

            var error = Marshal.GetLastPInvokeError();
            var outcome = error == (int)WIN32_ERROR.ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyRegistrationOutcome.Taken : HotkeyRegistrationOutcome.Failed;
            results.Add(new HotkeyRegistrationResult(action, outcome, error));
        }

        return results;
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered)
        {
            PInvoke.UnregisterHotKey(_window, Id(action));
        }

        _registered.Clear();
    }

    private sealed class ApplyRequest(IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> bindings) : IDisposable
    {
        public IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> Bindings { get; } = bindings;

        public ManualResetEventSlim Done { get; } = new();

        public List<HotkeyRegistrationResult>? Results { get; set; }

        public void Dispose() => Done.Dispose();
    }
}
