namespace Encore.Engine.Supervision;

/// <summary>Opens and starts the App for <see cref="AppSupervisor"/>; tests fake it.</summary>
internal interface IAppLauncher
{
    /// <summary>Opens the running process <paramref name="pid"/>, or returns <c>null</c> when it is gone.</summary>
    IWatchedProcess? Open(int pid);

    /// <summary>Starts the App in the tray; throws when it couldn't be started.</summary>
    IWatchedProcess Start();
}
