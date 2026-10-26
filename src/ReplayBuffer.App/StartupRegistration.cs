using System.Diagnostics;
using System.IO;
using System.Security;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App;

/// <summary>
/// Keeps the Run value in step with the engine's committed <c>startWithWindows</c> (R3, AD-16): the
/// one place the Run key policy lives. Only the installed instance touches the key; a dev or test
/// instance saves the setting and leaves the key alone. Start with Windows launches the App with
/// <c>--tray</c>, and the App starts its engine.
/// </summary>
internal sealed class StartupRegistration(Instance instance, RunKey runKey, string appPath)
{
    private bool? _synced;

    /// <summary>The revision whose registry write failed; heartbeats re-raise it, so it isn't retried.</summary>
    private long? _failedRevision;

    /// <summary>The Run value's data: the App's path, quoted, and <c>--tray</c>.</summary>
    public string Command { get; } = $"\"{appPath}\" {ShellApp.TrayArgument}";

    /// <summary>Whether this instance owns the Run value.</summary>
    public bool Applies => instance.Name == Instance.InstalledName;

    /// <summary>
    /// Applies the committed <c>startWithWindows</c> when it differs from the last one synced. The
    /// first call always reconciles, so a key that is missing, stale or points elsewhere is fixed at
    /// start. A failed registry write leaves the key as it is and is retried on the next committed
    /// revision, not on every state change that carries the same document.
    /// </summary>
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
