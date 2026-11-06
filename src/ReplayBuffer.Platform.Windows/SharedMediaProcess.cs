using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ReplayBuffer.Platform.Windows;

/// <summary>AD-28: hidden tool, seekable delete-shared stdin, explicit inherited handles only.</summary>
public static partial class SharedMediaProcess
{
    public static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string source,
        Func<Stream, CancellationToken, Task> readOutput, CancellationToken cancellationToken)
    {
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var errors = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        if (!DuplicateHandle(GetCurrentProcess(), file.SafeFileHandle.DangerousGetHandle(), GetCurrentProcess(),
                out var input, 0, true, 2))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var inputHandle = new SafeFileHandle(input, true);
        var handles = new[] { input, output.ClientSafePipeHandle.DangerousGetHandle(), errors.ClientSafePipeHandle.DangerousGetHandle() };
        var process = Start(executable, arguments, handles);
        using var processHandle = new SafeFileHandle(process, true);
        output.DisposeLocalCopyOfClientHandle();
        errors.DisposeLocalCopyOfClientHandle();
        using var registration = cancellationToken.Register(() => TerminateProcess(processHandle.DangerousGetHandle(), 1));
        var diagnostics = new StringBuilder();
        async Task GuardAsync(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch { TerminateProcess(process, 1); throw; }
        }
        var drain = GuardAsync(() => DrainAsync(errors, diagnostics));
        var consume = GuardAsync(() => readOutput(output, cancellationToken));
        try
        {
            await Task.WhenAll(consume, Task.Run(() => WaitForSingleObject(process, uint.MaxValue), CancellationToken.None)).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetExitCodeProcess(process, out var code) || code != 0)
            {
                throw new IOException($"Media tool failed ({code}): {diagnostics}");
            }
        }
        finally
        {
            // Only the process launched here. Never enumerate or stop other encoders/capture.
            TerminateProcess(process, 1);
            await Task.WhenAll(IgnoreAsync(consume), IgnoreAsync(drain)).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Preserve the initiating failure while draining owned pipes. */ }
    }

    private static async Task DrainAsync(Stream stream, StringBuilder tail)
    {
        using var reader = new StreamReader(stream);
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 8192) { tail.Remove(0, tail.Length - 8192); }
        }
    }

    /// <summary>Windows CommandLineToArgvW escaping, including trailing backslashes and empty args.</summary>
    public static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            text.Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static unsafe nint Start(string executable, IReadOnlyList<string> arguments, nint[] handles)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            initialized = true;
            fixed (nint* inherited = handles)
            {
                if (!UpdateProcThreadAttribute(list, 0, 0x00020002, (nint)inherited, (nuint)(handles.Length * sizeof(nint)), 0, 0))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                var startup = new StartupInfoEx
                {
                    Size = (uint)sizeof(StartupInfoEx), Flags = 0x100,
                    Input = handles[0], Output = handles[1], Error = handles[2], Attributes = list,
                };
                var command = (Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)) + '\0').ToCharArray();
                fixed (char* cmd = command)
                {
                    if (!CreateProcess(executable, cmd, 0, 0, true, 0x08084000, 0, null, ref startup, out var info))
                    { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    CloseHandle(info.Thread);
                    return info.Process;
                }
            }
        }
        finally
        {
            if (initialized) { DeleteProcThreadAttributeList(list); }
            Marshal.FreeHGlobal(list);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public nint ReservedData, Input, Output, Error, Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(string app, char* command, nint pa, nint ta, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint env, string? directory, ref StartupInfoEx startup, out ProcessInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [LibraryImport("kernel32.dll")] private static partial void DeleteProcThreadAttributeList(nint list);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateProcess(nint handle, uint code);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeProcess(nint handle, out uint code);
}
