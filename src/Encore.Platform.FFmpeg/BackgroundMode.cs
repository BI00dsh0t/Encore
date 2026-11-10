using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Encore.Platform.FFmpeg;

/// <summary>Background I/O and memory priority for the calling thread, so file work never competes with a game.</summary>
internal static class BackgroundMode
{
    /// <summary>Enters background mode; false, logged at debug level, when Windows refused it.</summary>
    public static bool Enter(ILogger logger)
    {
        if (TryEnter())
        {
            return true;
        }

        logger.LogDebug("The Clip writer could not enter background mode; writing at normal priority");
        return false;
    }

    /// <summary>Enters background mode; false when Windows refused it.</summary>
    public static bool TryEnter() =>
        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_BEGIN);

    /// <summary>Leaves background mode; call only on the thread that entered it.</summary>
    public static void Leave() =>
        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_END);
}
