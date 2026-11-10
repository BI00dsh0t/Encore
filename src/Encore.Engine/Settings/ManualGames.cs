using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Platform.Windows;

namespace Encore.Engine.Settings;

/// <summary>Hand-added games (story 3.6): <c>game.add</c> appends to <c>manualGames</c> (R1).</summary>
internal static class ManualGames
{
    /// <summary>
    /// Adds <paramref name="add"/>'s executable to <c>manualGames</c>, canonical (AD-18), through
    /// <see cref="SettingsStore.Update"/> (AD-4). A path already listed (ordinal ignore-case) writes
    /// nothing. A conflict with another write is retried.
    /// </summary>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.InvalidArguments"/>: the path isn't an existing executable.</exception>
    public static GameAdded Add(SettingsStore store, GameAdd add)
    {
        ArgumentNullException.ThrowIfNull(add);
        string path;
        try
        {
            path = GamePaths.Canonicalize(add.Path);
        }
        catch (Exception e) when (GamePaths.CanonicalizeFailed(e))
        {
            throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"The executable {add.Path} couldn't be opened: {e.Message}", e);
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"{add.Path} isn't an executable (.exe).");
        }

        SettingsRetry.Update(store, current =>
        {
            var list = current.Global.TryGetValue(Keys.ManualGames.Name, out var raw) && Keys.ManualGames.TryRead(raw, out var listed)
                ? listed
                : Keys.ManualGames.Default;
            return list.Contains(path, StringComparer.OrdinalIgnoreCase)
                ? null
                : new SettingsUpdate(current.Revision, [SettingsOp.SetGlobal(Keys.ManualGames.Name, Keys.ManualGames.ToJson([.. list, path]))]);
        });
        return new GameAdded(path);
    }
}
