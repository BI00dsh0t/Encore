using System.ComponentModel;
using System.Diagnostics;

namespace Encore.App.Engine;

/// <summary>What the App asks of the engine's process: whether it lives, whether it has exited, and to end it.</summary>
internal static class EngineProcess
{
    /// <summary>How long a closed connection waits for the engine's process to exit before treating it as alive.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    /// <summary>How long a kill waits for the engine's process to end.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    public static bool IsAlive(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Whether the engine exits within a few seconds of its connection closing.</summary>
    public static async Task<bool> HasExitedAsync(Process engine, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await engine.WaitForExitAsync(cancellationToken).WaitAsync(ExitWait, time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Kills the engine through the handle opened, and verified, at connect.</summary>
    public static async Task KillAsync(Process engine, TimeProvider time)
    {
        try
        {
            engine.Kill();
            await engine.WaitForExitAsync().WaitAsync(KillWait, time).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or TimeoutException)
        {
            // It exited meanwhile, or won't die; the next launch fails on the engine mutex and is retried.
        }
    }
}
