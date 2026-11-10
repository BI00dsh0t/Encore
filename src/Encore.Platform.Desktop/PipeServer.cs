using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Encore.Platform.Desktop;

/// <summary>Who serves a named pipe the App connected to (AD-3's server check).</summary>
public static class PipeServer
{
    /// <summary>The process id of the server of the pipe whose client end is <paramref name="pipe"/>.</summary>
    /// <exception cref="Win32Exception">The handle is not the client end of a named pipe.</exception>
    public static int ProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!PInvoke.GetNamedPipeServerProcessId(pipe, out var pid))
        {
            throw new Win32Exception();
        }

        return checked((int)pid);
    }
}
