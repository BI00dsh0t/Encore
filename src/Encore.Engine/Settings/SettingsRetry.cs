using Encore.Contracts;
using Encore.Contracts.Settings;

namespace Encore.Engine.Settings;

/// <summary>A read-then-write through <see cref="SettingsStore.Update"/>, retried on a conflict.</summary>
internal static class SettingsRetry
{
    public const int Tries = 3;

    /// <summary>Builds an update from the current document and writes it, rebuilding when another write landed first.</summary>
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
