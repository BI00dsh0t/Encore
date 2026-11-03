using System.ComponentModel;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Settings;

/// <summary>Hand-added games (story 3.6): <c>game.add</c> appends to <c>manualGames</c> (R1).</summary>
internal static class ManualGames
{
    private const int Tries = 3;

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
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"The executable {add.Path} couldn't be opened: {e.Message}", e);
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"{add.Path} isn't an executable (.exe).");
        }

        for (var attempt = 1; ; attempt++)
        {
            var current = store.Current;
            var list = current.Global.TryGetValue(Keys.ManualGames.Name, out var raw) && Keys.ManualGames.TryRead(raw, out var listed)
                ? listed
                : Keys.ManualGames.Default;
            if (list.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return new GameAdded(path);
            }

            try
            {
                store.Update(new SettingsUpdate(current.Revision, [SettingsOp.SetGlobal(Keys.ManualGames.Name, Keys.ManualGames.ToJson([.. list, path]))]));
                return new GameAdded(path);
            }
            catch (ErrorCodeException e) when (e.Code == ErrorCodes.SettingsConflict && attempt < Tries)
            {
                // Another write landed first: read it and try again.
            }
        }
    }
}
