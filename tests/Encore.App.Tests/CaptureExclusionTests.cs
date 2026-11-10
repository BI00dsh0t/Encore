using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Encore.App.Overlay;
using Encore.Contracts.Messages;
using Encore.Platform.Desktop;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class CaptureExclusionTests
{
    [Fact]
    public void EveryWindowTheAppShowsAllowsCapture() => Sta.Run(() =>
    {
        MainWindow? main = null;
        OverlayWindow? overlay = null;
        ContextMenu? menu = null;
        Window? plain = null;
        Popup? popup = null;
        try
        {
            var engine = new FakeEngineSession { Reply = (_, _) => new SettingsUpdateResult(1) };
            var model = new MainViewModel(engine, action => action());
            main = new MainWindow
            {
                DataContext = model,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
            };
            // The notice expires on a timer thread later, so the Overlay's updates go through the dispatcher.
            var dispatcher = Dispatcher.CurrentDispatcher;
            overlay = new OverlayWindow(new OverlayViewModel(engine, action => dispatcher.BeginInvoke(action)));
            menu = ShellApp.CreateTrayMenu(model, () => { }, () => { });
            plain = OffScreen(new Window { Width = 200, Height = 100 });
            popup = new Popup
            {
                Child = new Border { Width = 50, Height = 50, Background = Brushes.Gray },
                Placement = PlacementMode.AbsolutePoint,
                HorizontalOffset = -32000,
                VerticalOffset = -32000,
            };
            main.Show();
            engine.State = engine.State! with
            {
                Warnings = [new Warning("a", "clip-saved", WarningSeverity.Info, "capture", "Clip saved", DateTimeOffset.UtcNow, Overlay: true, Sound: null, Dismissable: true)],
            };
            engine.RaiseChanged();
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = -32000;
            menu.VerticalOffset = -32000;
            menu.IsOpen = true;
            plain.Show();
            popup.IsOpen = true;
            Sta.Pump(TimeSpan.FromMilliseconds(300));
            Assert.True(overlay.IsVisible);

            var windows = CaptureExclusion.VisibleWindows();
            Assert.NotEmpty(windows);
            Assert.Contains(new WindowInteropHelper(plain).Handle, windows);
            Assert.Contains(((HwndSource)PresentationSource.FromVisual(popup.Child)).Handle, windows);
            Assert.All(windows, w => Assert.False(CaptureExclusion.IsApplied(w), $"Window {w:X} is excluded."));
        }
        finally
        {
            popup?.IsOpen = false;
            menu?.IsOpen = false;
            plain?.Close();
            overlay?.Close();
            main?.CloseForQuit();
        }
    });

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void ADialogAllowsCaptureAndAScreenCopyShowsTheAppsWindow() => Sta.Run(() =>
    {
        using var bench = BenchLock.Take();

        // The red app window sits above the lime helper and should appear in the screen copy.
        var lime = Helper(Brushes.Lime);
        Window? red = null;
        try
        {
            lime.Show();
            var limeHwnd = new WindowInteropHelper(lime).Handle;
            OverlayWindowStyle.ShowWithoutActivation(limeHwnd, 100, 100, 400, 300);
            Sta.Pump(TimeSpan.FromMilliseconds(300));

            red = Helper(Brushes.Red);
            red.Show();
            const int X = 150, Y = 150, Width = 200, Height = 150;
            OverlayWindowStyle.ShowWithoutActivation(new WindowInteropHelper(red).Handle, X, Y, Width, Height);
            Sta.Pump(TimeSpan.FromMilliseconds(500));
            Assert.False(CaptureExclusion.IsApplied(limeHwnd));

            Exception? failure = null;
            var checkedDialog = false;
            var seen = 0;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var poll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
            poll.Tick += (_, _) =>
            {
                var dialog = CaptureExclusion.VisibleWindows().FirstOrDefault(w => ClassName(w) == "#32770");
                if (dialog == 0)
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        poll.Stop();
                        failure = new TimeoutException("The file dialog never showed.");
                    }

                    return;
                }

                // Let the dialog finish showing before checking its native window.
                if (++seen < 3)
                {
                    return;
                }

                poll.Stop();
                try
                {
                    var windows = CaptureExclusion.VisibleWindows();
                    Assert.All(
                        windows,
                        w => Assert.False(CaptureExclusion.IsApplied(w), $"Window {w:X} ({ClassName(w)}) is excluded."));
                    checkedDialog = true;
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    PostMessage(dialog, WmClose, 0, 0);
                }
            };

            var frame = new DispatcherFrame();
            var open = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
            open.Tick += (_, _) =>
            {
                open.Stop();
                poll.Start();
                new Microsoft.Win32.OpenFileDialog().ShowDialog(red);
                frame.Continue = false;
            };
            open.Start();
            Dispatcher.PushFrame(frame);

            if (failure is not null)
            {
                throw failure;
            }

            Assert.True(checkedDialog);
            Sta.Pump(TimeSpan.FromMilliseconds(300));
            AssertCopyIsRed(X, Y, Width, Height);
        }
        finally
        {
            red?.Close();
            lime.Close();
        }
    });

    private const uint WmClose = 0x0010;

    private static Window OffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static Window Helper(Brush background) => new ShellWindow()
    {
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowActivated = false,
        ShowInTaskbar = false,
        Topmost = true,
        Background = background,
    };

    private static void AssertCopyIsRed(int x, int y, int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
        }

        var reds = 0;
        for (var px = 0; px < width; px++)
        {
            for (var py = 0; py < height; py++)
            {
                var c = bitmap.GetPixel(px, py);
                reds += c.R > 150 && c.G < 100 && c.B < 100 ? 1 : 0;
            }
        }

        Assert.True(reds > width * height / 2, $"Only {reds} of {width * height} pixels were red.");
    }

    private static string ClassName(nint hwnd)
    {
        var name = new char[256];
        var length = GetClassNameW(hwnd, name, name.Length);
        return new string(name, 0, Math.Max(length, 0));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, [Out] char[] name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>The bench lock in the main folder: on-screen tests wait while another session holds it, then hold it.</summary>
    private sealed class BenchLock(string path) : IDisposable
    {
        public static BenchLock Take()
        {
            var path = Path.Combine(MainFolder(), ".bench-running");
            while (true)
            {
                try
                {
                    // CreateNew makes the check and the take one step, so two runs can't both hold it.
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    file.Write("App capture-visibility screen copy test"u8);
                    return new BenchLock(path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            }
        }

        public void Dispose() => File.Delete(path);

        // The topmost folder with Encore.slnx: the main checkout, not a worktree under it.
        private static string MainFolder()
        {
            string? found = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Encore.slnx")))
                {
                    found = dir.FullName;
                }
            }

            return found ?? throw new InvalidOperationException("Encore.slnx wasn't found above the test.");
        }
    }
}
