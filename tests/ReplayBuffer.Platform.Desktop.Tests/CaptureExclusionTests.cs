using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ReplayBuffer.Platform.Desktop.Tests;

public sealed class CaptureExclusionTests
{
    [Fact]
    public void ApplyExcludesAWindowFromCapture() => OnSta(() =>
    {
        var window = new Window();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Assert.False(CaptureExclusion.IsApplied(hwnd));

            CaptureExclusion.Apply(hwnd);

            Assert.True(CaptureExclusion.IsApplied(hwnd));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ApplyToAHandleThatIsNoWindowThrows()
    {
        Assert.Throws<Win32Exception>(() => CaptureExclusion.Apply(0));
        Assert.Throws<Win32Exception>(() => CaptureExclusion.IsApplied(0));
    }

    [Fact]
    public void TheHookExcludesANewTopLevelWindowAndSkipsItsNativeChild() => OnSta(() =>
    {
        using var hook = CaptureExclusion.ExcludeNewWindows();
        var window = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 200,
            Height = 100,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        HwndSource? child = null;
        try
        {
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;

            // WS_CHILD | WS_VISIBLE: the hook sees its create and show events and must skip them.
            child = new HwndSource(new HwndSourceParameters("child", 50, 50)
            {
                WindowStyle = 0x40000000 | 0x10000000,
                ParentWindow = hwnd,
            });
            Pump(TimeSpan.FromMilliseconds(300));

            Assert.True(CaptureExclusion.IsApplied(hwnd));
            Assert.Contains(hwnd, CaptureExclusion.VisibleWindows());

            // The hook's filter takes the window and skips its child (0x8002 is EVENT_OBJECT_SHOW).
            Assert.True(CaptureExclusion.NamesATopLevelWindow(0x8002, hwnd, 0, 0));
            Assert.False(CaptureExclusion.NamesATopLevelWindow(0x8002, child.Handle, 0, 0));
        }
        finally
        {
            child?.Dispose();
            window.Close();
        }
    });

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
