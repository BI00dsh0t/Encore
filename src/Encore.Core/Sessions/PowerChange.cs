namespace Encore.Core.Sessions;

/// <summary>A power change <see cref="IPowerNotifications"/> reports.</summary>
public enum PowerChange
{
    /// <summary>The PC is going to sleep.</summary>
    Suspending,

    /// <summary>The PC woke from sleep.</summary>
    Resumed,
}
