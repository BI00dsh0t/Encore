using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The App's one way to change settings: an update against the revision the caller rendered. It never retries.</summary>
internal sealed class SettingsSender(IEngineSession engine)
{
    public async Task<SettingsSendResult> UpdateAsync(
        SettingsDocument basis,
        IReadOnlyList<SettingsOp> ops,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        try
        {
            var update = new SettingsUpdate(basis.Revision, ops);
            var result = await engine.SendAsync(SettingsMessages.Update, update, cancellationToken).ConfigureAwait(false);
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
