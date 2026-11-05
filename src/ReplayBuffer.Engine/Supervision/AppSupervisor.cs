using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Engine.Ipc;

namespace ReplayBuffer.Engine.Supervision;

/// <summary>A process the engine watches: the attached App, or one it started.</summary>
internal interface IWatchedProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code once the process has exited.</summary>
    Task<int> Exited { get; }
}

/// <summary>Opens and starts the App for <see cref="AppSupervisor"/>; tests fake it.</summary>
internal interface IAppLauncher
{
    /// <summary>Opens the running process <paramref name="pid"/>, or returns <c>null</c> when it is gone.</summary>
    IWatchedProcess? Open(int pid);

    /// <summary>Starts the App in the tray.</summary>
    /// <exception cref="Exception">The App couldn't be started.</exception>
    IWatchedProcess Start();
}

/// <summary>
/// The engine's half of two-way supervision (AD-16). <c>app.attach</c> names the App to watch; when
/// that App exits while no <c>engine.shutdown</c> has been received, the engine starts it again
/// with backoff and raises <c>app-restarted</c>. It relaunches only an App that attached, or one it
/// started itself that then failed (a non-zero exit before attaching). After
/// <see cref="Contracts.Supervision.CrashLoopRestarts"/> relaunches within
/// <see cref="Contracts.Supervision.CrashLoopWindow"/>, the next exit is a crash loop: it logs and raises
/// <c>app-crash-loop</c> and stops watching, until an App attaches again.
/// </summary>
internal sealed class AppSupervisor(EventHub hub, Warnings warnings, IAppLauncher launcher, TimeProvider time, ILogger log) : IDisposable
{
    /// <summary>Makes the check and the raise of <see cref="RaiseEngineRestarted"/> one step.</summary>
    private static readonly Lock RestartedLock = new();

    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly RestartBackoff _backoff = new(time);
    private readonly CrashLoopGuard _guard = new(time);
    private int? _appId;
    private bool _attached;
    private bool _crashLooped;

    /// <summary>The active <c>app-crash-loop</c> warning, withdrawn when a new App attaches.</summary>
    private string? _crashLoopWarning;
    private int _generation;
    private bool _stopped;
    private int _disposed;

    /// <summary>The watch loop of the current App, for tests.</summary>
    internal Task Watching { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Raises <c>engine-restarted</c> for <paramref name="reason"/>, unless one is already active: the
    /// engine raises it at start from <c>serve --restarted</c>, and an <c>app.attach</c> that carries
    /// the reason too adds no second one.
    /// </summary>
    public static void RaiseEngineRestarted(EventHub hub, Warnings warnings, EngineRestartReason reason)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(warnings);
        lock (RestartedLock)
        {
            if (hub.State.Warnings.Any(w => w.Code == ErrorCodes.EngineRestarted))
            {
                return;
            }

            warnings.Raise(
                ErrorCodes.EngineRestarted,
                WarningSeverity.Warning,
                "supervision",
                reason == EngineRestartReason.Hung
                    ? "ReplayBuffer's background service stopped answering and was restarted."
                    : "ReplayBuffer's background service stopped unexpectedly and was restarted.",
                overlay: false);
        }
    }

    /// <summary>Handles <c>app.attach</c>.</summary>
    public Empty Attach(AppAttach attach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        lock (_lock)
        {
            if (_stopped)
            {
                return Empty.Value;
            }
        }

        hub.Publish(EngineMessages.Attached, new AppAttached(attach.Pid));
        if (attach.EngineRestart is { } reason)
        {
            RaiseEngineRestarted(hub, warnings, reason);
        }

        string? withdraw = null;
        lock (_lock)
        {
            if (_stopped)
            {
                return Empty.Value;
            }

            if (_crashLooped && _appId != attach.Pid)
            {
                // An App started by hand after a crash loop: relaunches are allowed again.
                _crashLooped = false;
                _guard.Reset();
                (withdraw, _crashLoopWarning) = (_crashLoopWarning, null);
                log.LogInformation("App {Pid} attached after a crash loop; relaunching it is allowed again", attach.Pid);
            }
        }

        if (withdraw is not null)
        {
            warnings.Withdraw(withdraw);
        }

        lock (_lock)
        {
            if (_stopped)
            {
                return Empty.Value;
            }

            if (_appId == attach.Pid)
            {
                _attached = true;
                return Empty.Value;
            }
        }

        var opened = launcher.Open(attach.Pid);
        if (opened is null)
        {
            log.LogWarning("App {Pid} attached but is already gone", attach.Pid);
            return Empty.Value;
        }

        lock (_lock)
        {
            if (_stopped)
            {
                opened.Dispose();
                return Empty.Value;
            }

            Watch(opened, attached: true);
        }

        log.LogInformation("Supervising App {Pid}", attach.Pid);
        return Empty.Value;
    }

    /// <summary>Stops supervising, for <c>engine.shutdown</c>: no App is relaunched after this.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _generation++;
            _appId = null;
            if (_disposed == 0)
            {
                _stop.Cancel();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            _stopped = true;
            _generation++;
            _appId = null;
            _stop.Cancel();
        }

        _stop.Dispose();
    }

    /// <summary>Makes <paramref name="app"/> the watched App; its watch loop owns it. Runs under the lock.</summary>
    private void Watch(IWatchedProcess app, bool attached)
    {
        _appId = app.Id;
        _attached = attached;
        var generation = ++_generation;
        Watching = WatchAsync(app, generation);
    }

    private async Task WatchAsync(IWatchedProcess app, int generation)
    {
        try
        {
            var code = await app.Exited.WaitAsync(_stop.Token).ConfigureAwait(false);

            // Each exit counts once against the crash-loop guard, however many starts its relaunch takes.
            var counted = false;
            while (true)
            {
                var crashLoop = false;
                lock (_lock)
                {
                    if (_stopped || generation != _generation)
                    {
                        return;
                    }

                    if (!_attached && code == 0)
                    {
                        // A started App that ended cleanly before attaching: another copy holds the instance.
                        log.LogInformation("The started App {Pid} exited with code 0 before attaching; not relaunching", app.Id);
                        return;
                    }

                    if (!counted)
                    {
                        counted = true;
                        crashLoop = _crashLooped = !_guard.TryRestart();
                    }
                }

                if (crashLoop)
                {
                    log.LogError(
                        "{Code}: App {Pid} exited with code {ExitCode} after {Restarts} relaunches within {Minutes} min; not relaunching it",
                        ErrorCodes.AppCrashLoop,
                        app.Id,
                        code,
                        Contracts.Supervision.CrashLoopRestarts,
                        Contracts.Supervision.CrashLoopWindow.TotalMinutes);
                    var raised = warnings.Raise(
                        ErrorCodes.AppCrashLoop,
                        WarningSeverity.Error,
                        "supervision",
                        "The app keeps stopping unexpectedly, so it isn't being restarted. Start it again to resume.",
                        overlay: false).Id;
                    var stale = false;
                    lock (_lock)
                    {
                        // An App that attached while this was being raised already ended the crash loop.
                        stale = !_crashLooped;
                        if (!stale)
                        {
                            _crashLoopWarning = raised;
                        }
                    }

                    if (stale)
                    {
                        warnings.Withdraw(raised);
                    }

                    return;
                }

                var delay = _backoff.Next();
                log.LogWarning("App {Pid} exited with code {Code} and no engine.shutdown; relaunching in {Delay} s", app.Id, code, delay.TotalSeconds);
                await Task.Delay(delay, time, _stop.Token).ConfigureAwait(false);

                IWatchedProcess started;
                try
                {
                    started = launcher.Start();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "The App couldn't be started; retrying");
                    continue;
                }

                lock (_lock)
                {
                    if (_stopped || generation != _generation)
                    {
                        // An App attached, or shutdown came, while this one was starting: leave it to its mutex.
                        started.Dispose();
                        return;
                    }

                    _appId = started.Id;
                    _attached = false;
                    generation = ++_generation;
                }

                log.LogInformation("Relaunched the App as {Pid}", started.Id);
                warnings.Raise(ErrorCodes.AppRestarted, WarningSeverity.Warning, "supervision", "The app stopped unexpectedly and was restarted.", overlay: false);
                app.Dispose();
                app = started;
                code = await started.Exited.WaitAsync(_stop.Token).ConfigureAwait(false);
                counted = false;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            app.Dispose();
        }
    }
}

/// <summary>
/// The real App: <c>ReplayBuffer.App.exe</c> beside the engine, started with <c>--tray</c>, and on a test
/// instance with <c>--instance</c> and any <paramref name="arguments"/> (<c>--replay-buffer</c>), so a
/// relaunched App launches its own engine the same way.
/// </summary>
internal sealed class AppLauncher(string appPath, Instance instance, IReadOnlyList<string>? arguments = null) : IAppLauncher
{
    public const string AppFileName = "ReplayBuffer.App.exe";

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
