using System.Diagnostics;
using System.IO;
using System.Security;
using Encore.App.Settings;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Platform.Desktop;

namespace Encore.App;

/// <summary>Keeps the Run value in step with the committed <c>startWithWindows</c>; only the installed instance writes it.</summary>
internal sealed class StartupRegistration(Instance instance, RunKey runKey, string appPath)
{
    private bool? _synced;

    /// <summary>The revision whose registry write failed; heartbeats re-raise it, so it isn't retried.</summary>
    private long? _failedRevision;

    /// <summary>The Run value's data: the App's path, quoted, and <c>--tray</c>.</summary>
    public string Command { get; } = CommandFor(appPath);

    /// <summary>The Run value's data for an App at <paramref name="appPath"/>.</summary>
    public static string CommandFor(string appPath) => $"\"{appPath}\" {AppArguments.TrayArgument}";

    /// <summary>Whether this instance owns the Run value.</summary>
    public bool Applies => instance.Name == Instance.InstalledName;

    /// <summary>Applies the committed setting when it differs from the last one synced; the first call always reconciles.</summary>
    public void Sync(SettingsDocument settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Applies)
        {
            return;
        }

        var on = SettingsValues.Global(settings, Keys.StartWithWindows);
        if (_synced == on || _failedRevision == settings.Revision)
        {
            return;
        }

        try
        {
            if (on)
            {
                if (runKey.Read() != Command)
                {
                    runKey.Write(Command);
                }
            }
            else
            {
                runKey.Delete();
            }

            _synced = on;
            _failedRevision = null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException)
        {
            _synced = null;
            _failedRevision = settings.Revision;
            Trace.TraceWarning($"Start with Windows: couldn't update the Run value: {e.Message}");
        }
    }
}
