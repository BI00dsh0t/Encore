using Encore.Contracts;

namespace Encore.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!ShellApp.TryParseArguments(args, out var instance))
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
