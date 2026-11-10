using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Encore.Platform.Windows;

/// <summary>Starts a hidden media tool with a seekable, delete-shared stdin; it inherits only the handles named here.</summary>
public static partial class SharedMediaProcess
{
    private const int DiagnosticsLimit = 8192;
    private const uint DuplicateSameAccess = 2;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const uint StartUseStdHandles = 0x100;

    // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | BELOW_NORMAL_PRIORITY_CLASS.
    private const uint CreationFlags = 0x08000000 | 0x00080000 | 0x00004000;

    public static async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string source,
        Func<Stream, CancellationToken, Task> readOutput,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var errors = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var input = InheritableCopy(file);
        using var inputHandle = new SafeFileHandle(input, true);
        var handles = new[]
        {
            input,
            output.ClientSafePipeHandle.DangerousGetHandle(),
            errors.ClientSafePipeHandle.DangerousGetHandle(),
        };
        var process = Start(executable, arguments, handles);
        using var processHandle = new SafeFileHandle(process, true);
        output.DisposeLocalCopyOfClientHandle();
        errors.DisposeLocalCopyOfClientHandle();
        using var registration = cancellationToken.Register(() => TerminateProcess(processHandle.DangerousGetHandle(), 1));
        var diagnostics = new StringBuilder();
        var drain = GuardAsync(process, () => DrainAsync(errors, diagnostics));
        var consume = GuardAsync(process, () => readOutput(output, cancellationToken));
        try
        {
            var exited = Task.Run(() => WaitForSingleObject(process, uint.MaxValue), CancellationToken.None);
            await Task.WhenAll(consume, exited).ConfigureAwait(false);
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

    /// <summary>Windows CommandLineToArgvW escaping, including trailing backslashes and empty args.</summary>
    public static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            text.Append('\\', character == '"' ? (slashes * 2) + 1 : slashes);
            text.Append(character);
            slashes = 0;
        }

        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    // An inheritable duplicate of the file's handle, for the tool's stdin; the caller closes it.
    private static nint InheritableCopy(FileStream file)
    {
        var self = GetCurrentProcess();
        if (!DuplicateHandle(self, file.SafeFileHandle.DangerousGetHandle(), self, out var copy, 0, true, DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return copy;
    }

    // A failed reader or drain ends the tool, so the other side's pipe closes too.
    private static async Task GuardAsync(nint process, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch
        {
            TerminateProcess(process, 1);
            throw;
        }
    }

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the initiating failure while draining owned pipes.
        }
    }

    private static async Task DrainAsync(Stream stream, StringBuilder tail)
    {
        using var reader = new StreamReader(stream);
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > DiagnosticsLimit)
            {
                tail.Remove(0, tail.Length - DiagnosticsLimit);
            }
        }
    }

    /// <summary>Starts the tool with an attribute list that lets it inherit only the three handles; returns its process handle.</summary>
    private static unsafe nint Start(string executable, IReadOnlyList<string> arguments, nint[] handles)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            initialized = true;
            fixed (nint* inherited = handles)
            {
                if (!UpdateProcThreadAttribute(
                    list, 0, ProcThreadAttributeHandleList, (nint)inherited, (nuint)(handles.Length * sizeof(nint)), 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // The handle list stays pinned until the process is created.
                return Create(executable, arguments, list, handles);
            }
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(list);
            }

            Marshal.FreeHGlobal(list);
        }
    }

    private static unsafe nint Create(string executable, IReadOnlyList<string> arguments, nint attributes, nint[] handles)
    {
        var startup = new StartupInfoEx
        {
            Size = (uint)sizeof(StartupInfoEx),
            Flags = StartUseStdHandles,
            Input = handles[0],
            Output = handles[1],
            Error = handles[2],
            Attributes = attributes,
        };
        var command = (Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)) + '\0').ToCharArray();
        fixed (char* commandLine = command)
        {
            if (!CreateProcess(executable, commandLine, 0, 0, true, CreationFlags, 0, null, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(info.Thread);
            return info.Process;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(
        string application,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags,
        nint environment,
        string? directory,
        ref StartupInfoEx startup,
        out ProcessInfo info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        nint sourceProcess,
        nint source,
        nint targetProcess,
        out nint target,
        uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint options);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint handle, uint code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint handle, out uint code);

    /// <summary><c>STARTUPINFOEXW</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XChars;
        public uint YChars;
        public uint Fill;
        public uint Flags;
        public ushort Show;
        public ushort ReservedSize;
        public nint ReservedData;
        public nint Input;
        public nint Output;
        public nint Error;
        public nint Attributes;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
