using ReplayBuffer.Contracts;

namespace ReplayBuffer.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!ShellApp.TryParseInstance(args, out var instance))
        {
            return 2;
        }

        // AD-16: one App per instance, taken before anything else and held for the App's life.
        // A second launch exits silently (user decision, 2026-10-25).
        using var single = SingleInstance.TryAcquire(SingleInstance.App, instance.Name);
        if (single is null)
        {
            return 0;
        }

        return new ShellApp(instance).Run();
    }
}
