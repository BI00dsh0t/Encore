using System.IO;

namespace Encore.App.Engine;

/// <summary>What the App needs to find, launch and watch its engine; tests change the timings.</summary>
internal sealed record EngineConnectionOptions
{
    /// <summary>The engine's file name; the App trusts only this image, in its own folder.</summary>
    public const string EngineFileName = "Encore.Engine.exe";

    public required string InstanceName { get; init; }

    /// <summary>The engine executable, launched when no engine answers.</summary>
    public required string EnginePath { get; init; }

    public IReadOnlyList<string> EngineArguments { get; init; } = ["serve"];

    /// <summary>The only engine image the App connects to, and so the only one it ever kills: the one in its own folder.</summary>
    public string TrustedEnginePath { get; init; } = Path.Combine(AppContext.BaseDirectory, EngineFileName);

    /// <summary>How long to keep trying to connect after launching the engine.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one connection attempt waits for the pipe and the snapshot.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The process <c>app.attach</c> names, which the engine relaunches if it dies.</summary>
    public int AppPid { get; init; } = Environment.ProcessId;

    /// <summary>Writes one line to the App's log; the supervise loop logs a crash loop through it.</summary>
    public Action<string>? Log { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
