using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>The outcome of a <see cref="SettingsSender.UpdateAsync"/>.</summary>
internal abstract record SettingsSendResult
{
    /// <summary>The engine committed the ops; <c>settings.changed</c> for <paramref name="Revision"/> has arrived or is on its way.</summary>
    public sealed record Committed(long Revision) : SettingsSendResult;

    /// <summary>
    /// <c>settings-conflict</c>: the document moved on since the caller rendered it. <paramref name="Current"/>
    /// is the document to reload; nothing was changed or retried.
    /// </summary>
    public sealed record Conflict(SettingsDocument Current) : SettingsSendResult;

    /// <summary>The engine refused the ops, or couldn't be reached.</summary>
    public sealed record Failed(string Code, string Message) : SettingsSendResult;
}

/// <summary>
/// The App's one way to change settings (AD-4): <c>settings.update</c> against the revision of the
/// document the caller rendered. It never retries (AD-3); a conflict comes back as a reload.
/// </summary>
internal sealed class SettingsSender(IEngineSession engine)
{
    public async Task<SettingsSendResult> UpdateAsync(SettingsDocument basis, IReadOnlyList<SettingsOp> ops, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        try
        {
            var result = await engine.SendAsync(SettingsMessages.Update, new SettingsUpdate(basis.Revision, ops), cancellationToken).ConfigureAwait(false);
            return new SettingsSendResult.Committed(result.Revision);
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.SettingsConflict)
        {
            // The settings.changed that moved the revision arrived before this reply, so the state already holds it.
            return new SettingsSendResult.Conflict(engine.State?.Settings ?? basis);
        }
        catch (ErrorCodeException e)
        {
            return new SettingsSendResult.Failed(e.Code, e.Message);
        }
    }
}

/// <summary>Typed reads and ops over a settings document's Global values. Profile values go through Core's <c>Resolve</c>, which the App doesn't reference.</summary>
internal static class SettingsValues
{
    /// <summary>The Global value of <paramref name="key"/>: the document's, or the key's default when it is missing or doesn't fit.</summary>
    public static T Global<T>(SettingsDocument document, SettingKey<T> key) =>
        document.Global.TryGetValue(key.Name, out var raw) && key.TryRead(raw, out var value) ? value : key.Default;

    public static SettingsOp SetGlobal<T>(SettingKey<T> key, T value) => SettingsOp.SetGlobal(key.Name, key.ToJson(value));
}
