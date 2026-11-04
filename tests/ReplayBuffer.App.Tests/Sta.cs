using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// The test classes that build WPF views or windows. WPF on several STA threads at once in one process
/// deadlocked the full run, so they take turns.
/// </summary>
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
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
}
