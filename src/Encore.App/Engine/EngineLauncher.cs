using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Encore.Contracts.Messages;

namespace Encore.App.Engine;

/// <summary>Starts the engine's process from the options' path and arguments.</summary>
internal static class EngineLauncher
{
    /// <summary>Starts the engine; after a crash or hang it passes the reason, so the engine raises <c>engine-restarted</c>.</summary>
    public static Process Launch(EngineConnectionOptions options, EngineRestartReason? restart)
    {
        var start = new ProcessStartInfo(options.EnginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.EnginePath) ?? "",
        };
        foreach (var argument in options.EngineArguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (restart is { } reason)
        {
            start.ArgumentList.Add("--restarted");
            start.ArgumentList.Add(reason == EngineRestartReason.Hung ? "hung" : "crashed");
        }

        return Process.Start(start) ?? throw new Win32Exception("The engine process didn't start.");
    }
}
