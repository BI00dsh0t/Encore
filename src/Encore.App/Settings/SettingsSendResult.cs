using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The outcome of a <see cref="SettingsSender.UpdateAsync"/>.</summary>
internal abstract record SettingsSendResult
{
    /// <summary>The engine committed the ops; <c>settings.changed</c> for that revision has arrived or is on its way.</summary>
    public sealed record Committed(long Revision) : SettingsSendResult;

    /// <summary><c>settings-conflict</c>: the document moved on since the caller rendered it; Current is the one to reload.</summary>
    public sealed record Conflict(SettingsDocument Current) : SettingsSendResult;

    /// <summary>The engine refused the ops, or couldn't be reached.</summary>
    public sealed record Failed(string Code, string Message) : SettingsSendResult;
}
