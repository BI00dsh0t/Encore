using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.Bench.Overlay;

/// <summary>
/// Story 5.2's Overlay host for <c>bench overlay</c>: <c>--pid &lt;game&gt;</c> picks the monitor. It prints
/// <c>ready 0x&lt;hwnd&gt; excluded=&lt;bool&gt; exstyle=0x&lt;hex&gt;</c> with the dot hidden, then reads
/// <c>show</c> and <c>hide</c> lines on stdin and answers each with <c>shown</c> or <c>hidden</c>. It exits at
/// <c>quit</c> or when stdin closes.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int? pid = null;
        if (args is ["--pid", var text])
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var given))
            {
                Console.Error.WriteLine($"error: --pid '{text}' is not a process id.");
                return 2;
            }

            pid = given;
        }
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: ReplayBuffer.Bench.Overlay [--pid <game process>]");
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new OverlayHostWindow(pid);
        var hwnd = window.Handle;
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ready 0x{hwnd:X} excluded={(CaptureExclusion.IsApplied(hwnd) ? "true" : "false")} exstyle=0x{OverlayWindowStyle.ExtendedStyle(hwnd):X}"));
        Console.Out.Flush();

        var dispatcher = Dispatcher.CurrentDispatcher;
        var reader = new Thread(() =>
        {
            while (Console.In.ReadLine() is { } line && line != "quit")
            {
                var reply = dispatcher.Invoke(() =>
                {
                    switch (line)
                    {
                        case "show":
                            window.ShowDot();
                            return "shown";
                        case "hide":
                            window.Hide();
                            return "hidden";
                        default:
                            return $"error: unknown command '{line}'";
                    }
                });
                Console.Out.WriteLine(reply);
                Console.Out.Flush();
            }

            dispatcher.BeginInvoke(app.Shutdown);
        })
        { IsBackground = true, Name = "Overlay host stdin" };
        reader.Start();
        return app.Run();
    }
}
