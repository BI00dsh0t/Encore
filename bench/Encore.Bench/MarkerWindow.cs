using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Encore.Bench;

/// <summary>
/// The drift run's video marker: a black 640×360 top-level window titled <see cref="Title"/> that turns
/// white at each whole second from its origin, for 3 frames at the output fps (and at least 3 DWM
/// compositions), then black again. Each flash is painted with GDI from a thread of its own, then
/// <c>DwmFlush</c> waits for the composition that shows it, and the QPC read right after is the
/// flash's stamp. The window lives on an STA thread of its own, so the caller's thread stays free.
/// </summary>
internal sealed class MarkerWindow : IDisposable
{
    public const string Title = "Encore Bench Marker";
    public const int Width = 640;
    public const int Height = 360;
    public const int FlashFrames = 3;

    private readonly Thread _ui;
    private readonly ManualResetEventSlim _shown = new();
    private readonly Lock _lock = new();
    private readonly List<long> _flashes = [];
    private readonly int _fps;
    private Form? _form;
    private Thread? _flasher;
    private volatile bool _stopping;

    private MarkerWindow(int fps)
    {
        _fps = fps;
        _ui = new Thread(RunUi) { Name = "Bench marker window", IsBackground = true };
        _ui.SetApartmentState(ApartmentState.STA);
        _ui.Start();
        if (!_shown.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new BenchFailure("The marker window did not open.");
        }

        if (Fault is { } fault)
        {
            throw new BenchFailure($"The marker window failed: {fault.Message}");
        }
    }

    public nint Handle { get; private set; }

    /// <summary>The first failure of the window or its flasher, if any.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>The QPC of each flash so far, ascending.</summary>
    public long[] FlashQpcs
    {
        get
        {
            lock (_lock)
            {
                return [.. _flashes];
            }
        }
    }

    /// <summary>Opens the window, black, and waits until it is shown.</summary>
    public static MarkerWindow Open(int fps) => new(fps);

    /// <summary>Flashes at <paramref name="originQpc"/> + k seconds, k = 0, 1, …, until disposed.</summary>
    public void StartFlashing(long originQpc)
    {
        if (_flasher is not null)
        {
            throw new InvalidOperationException("Already flashing.");
        }

        _flasher = new Thread(() => Flash(originQpc)) { Name = "Bench marker flasher", IsBackground = true, Priority = ThreadPriority.Highest };
        _flasher.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _flasher?.Join();
        try
        {
            _form?.BeginInvoke(() => _form.Close());
        }
        catch (InvalidOperationException)
        {
            // Already closed.
        }

        _ui.Join(TimeSpan.FromSeconds(5));
        _shown.Dispose();
    }

    private void RunUi()
    {
        try
        {
            using var form = new Form
            {
                Text = Title,
                BackColor = Color.Black,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
                ClientSize = new Size(Width, Height),
                AutoScaleMode = AutoScaleMode.None,
                TopMost = true,
                ShowInTaskbar = true,
            };
            form.Shown += (_, _) =>
            {
                Handle = form.Handle;
                _shown.Set();
            };
            _form = form;
            Application.Run(form);
        }
        catch (Exception e)
        {
            Fault ??= e;
            _shown.Set();
        }
    }

    private void Flash(long originQpc)
    {
        var frequency = Stopwatch.Frequency;
        var hold = (long)(frequency * (double)FlashFrames / _fps);
        _ = Native.TimeBeginPeriod(1);
        try
        {
            for (long k = 0; !_stopping; k++)
            {
                if (!WaitUntil(originQpc + (k * frequency)))
                {
                    return;
                }

                Paint(white: true);
                Compose();
                var stamp = Stopwatch.GetTimestamp();
                lock (_lock)
                {
                    _flashes.Add(stamp);
                }

                for (var compositions = 1; compositions < FlashFrames || Stopwatch.GetTimestamp() - stamp < hold; compositions++)
                {
                    Compose();
                }

                Paint(white: false);
                Compose();
            }
        }
        catch (Exception e)
        {
            Fault ??= e;
        }
        finally
        {
            _ = Native.TimeEndPeriod(1);
        }
    }

    /// <summary>Sleeps to just before <paramref name="qpc"/>, then spins; false when stopping.</summary>
    private bool WaitUntil(long qpc)
    {
        var frequency = Stopwatch.Frequency;
        while (!_stopping)
        {
            var left = qpc - Stopwatch.GetTimestamp();
            if (left <= 0)
            {
                return true;
            }

            var ms = left * 1000 / frequency;
            if (ms > 3)
            {
                Thread.Sleep((int)Math.Min(ms - 2, 100));
            }
            else
            {
                Thread.SpinWait(100);
            }
        }

        return false;
    }

    /// <summary>Waits for the next DWM composition; falls back to a 1 ms sleep if DwmFlush returns at once.</summary>
    private static void Compose()
    {
        var before = Stopwatch.GetTimestamp();
        if (Native.DwmFlush() < 0 || Stopwatch.GetTimestamp() - before < Stopwatch.Frequency / 2000)
        {
            Thread.Sleep(1);
        }
    }

    private void Paint(bool white)
    {
        var hwnd = Handle;
        var dc = Native.GetDC(hwnd);
        if (dc == 0)
        {
            return;
        }

        try
        {
            var rect = new Native.Rect { Right = Width, Bottom = Height };
            _ = Native.GetClientRect(hwnd, ref rect);
            _ = Native.FillRect(dc, ref rect, Native.GetStockObject(white ? Native.WhiteBrush : Native.BlackBrush));
            _ = Native.GdiFlush();
        }
        finally
        {
            _ = Native.ReleaseDC(hwnd, dc);
        }
    }

    private static class Native
    {
        public const int WhiteBrush = 0;
        public const int BlackBrush = 4;

        [DllImport("user32.dll")]
        public static extern nint GetDC(nint hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(nint hwnd, nint dc);

        [DllImport("user32.dll")]
        public static extern int FillRect(nint dc, ref Rect rect, nint brush);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint hwnd, ref Rect rect);

        [DllImport("gdi32.dll")]
        public static extern nint GetStockObject(int index);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GdiFlush();

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint ms);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint TimeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
