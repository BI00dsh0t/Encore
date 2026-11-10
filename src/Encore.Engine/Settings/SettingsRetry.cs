using Encore.Contracts;
using Encore.Contracts.Settings;

namespace Encore.Engine.Settings;

/// <summary>A read-then-write through <see cref="SettingsStore.Update"/> (AD-4), retried on a conflict.</summary>
internal static class SettingsRetry
{
    public const int Tries = 3;

    /// <summary>
    /// Builds an update from <see cref="SettingsStore.Current"/> and writes it, reading again and
    /// rebuilding when another write landed first, up to <see cref="Tries"/> times.
    /// </summary>
    /// <param name="build">The update for a document, or null when there is nothing to write.</param>
    /// <returns>True once a write commits; false when <paramref name="build"/> returned null.</returns>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.SettingsConflict"/> on the last try, or any other failure of the write.</exception>
    public static bool Update(SettingsStore store, Func<SettingsDocument, SettingsUpdate?> build)
    {
        for (var attempt = 1; ; attempt++)
        {
            var update = build(store.Current);
            if (update is null)
            {
                return false;
            }

            try
            {
                store.Update(update);
                return true;
            }
            catch (ErrorCodeException e) when (e.Code == ErrorCodes.SettingsConflict && attempt < Tries)
            {
                // Another write landed first: read it and try again.
            }
        }
    }
}
