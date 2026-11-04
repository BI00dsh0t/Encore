using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// The attached monitors and their changes (T-03) for the engine: the list is
/// <see cref="MonitorFinder.Describe"/>'s, and a change is <c>WM_DISPLAYCHANGE</c>, which Windows
/// broadcasts to top-level windows only (a message-only window never gets it). While anyone is
/// subscribed, one thread owns a hidden, never-shown top-level window; a burst of changes (a hot-plug
/// sends several) is reported once, <see cref="Settle"/> after the last. It reads the displays only and
/// never changes them. AOT-safe: blittable structs and an unmanaged window procedure.
/// </summary>
public sealed unsafe partial class DisplayMonitors(ILogger logger) : IDisplayMonitors
{
    /// <summary>How long after the last <c>WM_DISPLAYCHANGE</c> a change is reported, so the list has settled.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private const uint WmDisplayChange = 0x007E;
    private const uint WmTimer = 0x0113;
    private const uint WmStop = 0x8000 + 1; // WM_APP + 1
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const int ErrorClassAlreadyExists = 1410;
    private const nuint SettleTimer = 1;
    private const string ClassName = "ReplayBuffer.DisplayChanges";

    // Each live window's listener, by window handle: the window procedure is static.
    private static readonly ConcurrentDictionary<nint, Listener> Listeners = new();
    private static readonly Lock ClassLock = new();
    private static bool _classRegistered;

    private readonly Lock _lock = new();
    private readonly List<Action> _subscribers = [];
    private Listener? _listener;

    /// <inheritdoc/>
    public IReadOnlyList<DisplayMonitor> List()
    {
        try
        {
            return [.. MonitorFinder.Describe().Select(m => new DisplayMonitor(m.DeviceName, m.FriendlyName, m.Width, m.Height, m.IsPrimary))];
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The monitors couldn't be listed");
            return [];
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        lock (_lock)
        {
            _subscribers.Add(changed);
            if (_listener is null)
            {
                try
                {
                    _listener = new Listener(this);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Display changes can't be watched");
                }
            }
        }

        return new Subscription(this, changed);
    }

    /// <summary>
    /// For tests: posts <c>WM_DISPLAYCHANGE</c> to this source's own hidden window only, as Windows'
    /// broadcast would; nothing about the displays changes. False when nothing is subscribed.
    /// </summary>
    internal bool SimulateDisplayChange()
    {
        lock (_lock)
        {
            return _listener is { } listener && PostMessageW(listener.Window, WmDisplayChange, 32, 0);
        }
    }

    private void Unsubscribe(Action changed)
    {
        Listener? stop = null;
        lock (_lock)
        {
            _subscribers.Remove(changed);
            if (_subscribers.Count == 0)
            {
                (stop, _listener) = (_listener, null);
            }
        }

        stop?.Dispose();
    }

    private void Raise()
    {
        Action[] subscribers;
        lock (_lock)
        {
            subscribers = [.. _subscribers];
        }

        logger.LogInformation("The displays changed");
        foreach (var changed in subscribers)
        {
            try
            {
                changed();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A display-change subscriber failed");
            }
        }
    }

    private static void RegisterClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
            {
                return;
            }

            fixed (char* name = ClassName)
            {
                var windowClass = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx),
                    WndProc = &WndProc,
                    Instance = GetModuleHandleW(null),
                    ClassName = name,
                };
                if (RegisterClassExW(&windowClass) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == WmDisplayChange)
            {
                // Restarted by each change of a burst; it fires once, Settle after the last.
                SetTimer(window, SettleTimer, (uint)Settle.TotalMilliseconds, null);
                return 0;
            }

            if (message == WmTimer && wParam == SettleTimer)
            {
                KillTimer(window, SettleTimer);
                if (Listeners.TryGetValue(window, out var listener))
                {
                    listener.Owner.Raise();
                }

                return 0;
            }
        }
        catch (Exception)
        {
            // Never into the window manager.
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(char* name);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, void* param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint window, nuint id, uint elapse, void* timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint window, nuint id);

    /// <summary><c>WNDCLASSEXW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    /// <summary><c>MSG</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    /// <summary>The thread that owns the hidden window and pumps its messages.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Exception? _startFailure;
        private nint _window;

        public Listener(DisplayMonitors owner)
        {
            Owner = owner;
            _thread = new Thread(Run) { Name = "DisplayChanges", IsBackground = true };
            _thread.Start();
            _ready.Wait();
            _ready.Dispose();
            if (_startFailure is not null)
            {
                _thread.Join();
                throw new InvalidOperationException("The display-change window couldn't be created.", _startFailure);
            }
        }

        public DisplayMonitors Owner { get; }

        public nint Window => _window;

        public void Dispose()
        {
            if (PostMessageW(_window, WmStop, 0, 0) && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            {
                _thread.Join();
            }
        }

        private void Run()
        {
            try
            {
                RegisterClass();
                fixed (char* name = ClassName)
                {
                    // Top-level (no parent) so the broadcast reaches it; a popup that is never shown.
                    _window = CreateWindowExW(WsExToolWindow, name, null, WsPopup, 0, 0, 0, 0, 0, 0, GetModuleHandleW(null), null);
                }

                if (_window == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                Listeners[_window] = this;
            }
            catch (Exception e)
            {
                _startFailure = e;
                _ready.Set();
                return;
            }

            _ready.Set();
            Msg message;
            while (GetMessageW(&message, 0, 0, 0) > 0)
            {
                if (message.Message == WmStop)
                {
                    break;
                }

                DispatchMessageW(&message);
            }

            Listeners.TryRemove(_window, out _);
            KillTimer(_window, SettleTimer);
            DestroyWindow(_window);
        }
    }

    private sealed class Subscription(DisplayMonitors owner, Action changed) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(changed);
            }
        }
    }
}
