using System.IO;
using Encore.Platform.Desktop;
using Velopack;

namespace Encore.App.Updates;

/// <summary>What the installer asks the App to do as it installs, updates or uninstalls it. Runs before anything else.</summary>
internal static class InstallerHooks
{
    /// <summary>Handles an installer call and exits when it was one; otherwise returns at once.</summary>
    public static void Run() =>
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => RepointStartup())
            .OnAfterUpdateFastCallback(_ => RepointStartup())
            .OnBeforeUninstallFastCallback(_ => RunKey.Startup.Delete())
            .Run();

    /// <summary>Keeps Start with Windows on, pointing at the launcher, when it was on before.</summary>
    private static void RepointStartup()
    {
        if (RunKey.Startup.Read() is null || Environment.ProcessPath is not { } appPath)
        {
            return;
        }

        var launcher = InstallLayout.LaunchPath(appPath, File.Exists);
        RunKey.Startup.Write(StartupRegistration.CommandFor(launcher));
    }
}
