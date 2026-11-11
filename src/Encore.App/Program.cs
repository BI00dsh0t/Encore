using Encore.App.Updates;
using Encore.Contracts;

namespace Encore.App;

/// <summary>Starts the App: reads the command line, then runs <see cref="ShellApp"/> on the UI thread.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The installer calls the App with its own arguments while it installs, updates or uninstalls; those calls end here.
        InstallerHooks.Run();

        if (!AppArguments.TryParse(args, out var instance))
        {
            return 2;
        }

        // One App per instance, taken before anything else and held for the App's life; a second launch exits silently.
        using var single = SingleInstance.TryAcquire(SingleInstance.App, instance.Name);
        if (single is null)
        {
            return 0;
        }

        return new ShellApp(instance).Run();
    }
}
