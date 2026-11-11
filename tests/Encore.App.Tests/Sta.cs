using System.Runtime.ExceptionServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Encore.App.Tests;

/// <summary>The test classes that build WPF views; WPF on several STA threads at once deadlocked, so they take turns.</summary>
[CollectionDefinition(nameof(WpfCollection), DisableParallelization = true)]
public sealed class WpfCollection;

/// <summary>Runs WPF test code on a thread of its own in a single-threaded apartment.</summary>
internal static class Sta
{
    public static void Run(Action action)
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
            finally
            {
                ShutDownDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    // A dispatcher left running keeps its windows until the process exits, and 200 of them made the test host exit too slowly.
    private static void ShutDownDispatcher()
    {
        // A popup that still holds the mouse would ask WPF's pen thread during shutdown, after that thread has gone.
        Mouse.Capture(null);
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    /// <summary>Runs the current thread's dispatcher for <paramref name="duration"/>, so windows show, render and settle.</summary>
    public static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Pumps the dispatcher until the condition holds, failing after five seconds, as a loaded machine can be slow.</summary>
    public static void PumpUntil(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Timed out waiting for {what}.");
            Pump(TimeSpan.FromMilliseconds(20));
        }
    }
}
