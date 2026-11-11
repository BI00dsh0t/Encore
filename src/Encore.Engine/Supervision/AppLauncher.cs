using System.ComponentModel;
using System.Diagnostics;
using Encore.Contracts;

namespace Encore.Engine.Supervision;

/// <summary>The real App beside the engine, started with <c>--tray</c>; a test instance passes its own arguments on.</summary>
internal sealed class AppLauncher(string appPath, Instance instance, IReadOnlyList<string>? arguments = null) : IAppLauncher
{
    public const string AppFileName = "Encore.App.exe";

    public static AppLauncher Beside(Instance instance, IReadOnlyList<string>? arguments = null) =>
        new(Path.Combine(AppContext.BaseDirectory, AppFileName), instance, arguments);

    public IWatchedProcess? Open(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);

            // Open the handle now, so a reused pid can't stand in for it and the exit code stays readable.
            _ = process.SafeHandle;
            return new WatchedProcess(process);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public IWatchedProcess Start()
    {
        var start = new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? "",
        };
        start.ArgumentList.Add("--tray");
        if (instance.IsTest)
        {
            start.ArgumentList.Add("--instance");
            start.ArgumentList.Add(instance.Name);
            foreach (var argument in arguments ?? [])
            {
                start.ArgumentList.Add(argument);
            }
        }

        return new WatchedProcess(Process.Start(start) ?? throw new InvalidOperationException("The App process didn't start."));
    }

    private sealed class WatchedProcess(Process process) : IWatchedProcess
    {
        private Task<int>? _exited;

        public int Id => process.Id;

        public Task<int> Exited => _exited ??= WaitAsync();

        public void Dispose() => process.Dispose();

        private async Task<int> WaitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
