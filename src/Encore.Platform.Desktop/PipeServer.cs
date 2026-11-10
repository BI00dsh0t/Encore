using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Encore.Platform.Desktop;

/// <summary>Finds which process serves a named pipe the App connected to.</summary>
public static class PipeServer
{
    /// <summary>The process id of the pipe's server; throws Win32Exception when the handle is not a pipe's client end.</summary>
    public static int ProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!PInvoke.GetNamedPipeServerProcessId(pipe, out var processId))
        {
            throw new Win32Exception();
        }

        return checked((int)processId);
    }
}
