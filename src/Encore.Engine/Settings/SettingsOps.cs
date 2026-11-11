using System.Collections.Immutable;
using System.Text.Json;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Hotkeys;
using Encore.Core.Output;

namespace Encore.Engine.Settings;

/// <summary>The rules of a settings update: applies its ops to a document, refusing the first op that breaks one.</summary>
internal static class SettingsOps
{
    public static SettingsDocument Apply(SettingsDocument document, IReadOnlyList<SettingsOp> ops)
    {
        var global = document.Global.ToBuilder();
        var profiles = document.Profiles.ToBuilder();
        ProfileFolders.AssignMissing(profiles); // Legacy profiles first, as ProfileFolders.FolderOf sees them.

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i] ?? throw Refuse(ErrorCodes.InvalidArguments, i, "the op is null");
            if (!Enum.IsDefined(op.Scope) || !Enum.IsDefined(op.Op) || op.Key is null)
            {
                throw Refuse(ErrorCodes.InvalidArguments, i, "the op has no valid scope, op or key");
            }

            if (op.Scope == SettingsScope.Global)
            {
                ApplyGlobal(global, op, i);
            }
            else
            {
                ApplyProfile(profiles, op, i);
            }
        }

        RefuseDuplicateHotkeys(global, ops);
        ProfileFolders.AssignMissing(profiles);
        return document with { Global = global.ToImmutable(), Profiles = profiles.ToImmutable() };
    }

    /// <summary>An update that sets <c>hotkeys</c> with two actions on one combination is refused.</summary>
    private static void RefuseDuplicateHotkeys(ImmutableSortedDictionary<string, JsonElement>.Builder global, IReadOnlyList<SettingsOp> ops)
    {
        var index = -1;
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is { Scope: SettingsScope.Global, Op: SettingsOpKind.Set } op && op.Key == Keys.Hotkeys.Name)
            {
                index = i;
            }
        }

        if (index >= 0 && global.TryGetValue(Keys.Hotkeys.Name, out var raw) && Keys.Hotkeys.TryRead(raw, out var hotkeys)
            && HotkeyPlan.FindDuplicate(hotkeys) is { } duplicate)
        {
            throw Refuse(ErrorCodes.HotkeyDuplicate, index, $"'{duplicate.First}' and '{duplicate.Second}' have the same Hotkey");
        }
    }

    private static void ApplyGlobal(ImmutableSortedDictionary<string, JsonElement>.Builder global, SettingsOp op, int index)
    {
        if (op.ProfileId is not null)
        {
            throw Refuse(ErrorCodes.InvalidArguments, index, "a global op has no profileId");
        }

        var key = KeyFor(op, index);
        if (op.Op == SettingsOpKind.Remove)
        {
            global.Remove(key.Name);
        }
        else
        {
            global[key.Name] = Normalize(key, op, index);
        }
    }

    private static void ApplyProfile(ImmutableSortedDictionary<string, GameProfile>.Builder profiles, SettingsOp op, int index)
    {
        if (string.IsNullOrEmpty(op.ProfileId))
        {
            throw Refuse(ErrorCodes.ProfileNotFound, index, "a profile op needs a profileId");
        }

        var id = op.ProfileId;
        profiles.TryGetValue(id, out var profile);

        if (ProfileAttributes.All.Contains(op.Key, StringComparer.Ordinal))
        {
            profiles[id] = ApplyAttribute(profile, op, index);
            return;
        }

        var key = KeyFor(op, index);
        if (!key.Overridable)
        {
            throw Refuse(ErrorCodes.KeyNotOverridable, index, $"'{key.Name}' is Global-only");
        }

        if (profile is null)
        {
            throw Refuse(ErrorCodes.ProfileNotFound, index, $"no Game Profile '{id}'");
        }

        var overrides = op.Op == SettingsOpKind.Remove
            ? profile.Overrides.Remove(key.Name)
            : profile.Overrides.SetItem(key.Name, Normalize(key, op, index));
        profiles[id] = profile with { Overrides = overrides };
    }

    private static GameProfile ApplyAttribute(GameProfile? profile, SettingsOp op, int index)
    {
        if (op.Op == SettingsOpKind.Remove)
        {
            throw Refuse(ErrorCodes.SettingInvalidValue, index, $"the profile attribute '{op.Key}' can't be removed");
        }

        if (op.Key == ProfileAttributes.Name)
        {
            if (op.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(op.Value.GetString()))
            {
                throw Refuse(ErrorCodes.SettingInvalidValue, index, "'name' must be a non-empty string");
            }

            var name = op.Value.GetString()!;
            return profile is null ? new GameProfile { Name = name } : profile with { Name = name };
        }

        if (profile is null)
        {
            throw Refuse(ErrorCodes.ProfileNotFound, index, $"no Game Profile '{op.ProfileId}'");
        }

        if (op.Key == ProfileAttributes.Source)
        {
            return profile with { Source = SourceOf(op, index) };
        }

        if (op.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Refuse(ErrorCodes.SettingInvalidValue, index, $"'{op.Key}' must be true or false");
        }

        var value = op.Value.GetBoolean();
        return op.Key == ProfileAttributes.Ignored
            ? profile with { Ignored = value }
            : profile with { RecordingsEnabled = value };
    }

    private static string SourceOf(SettingsOp op, int index) =>
        op.Value.ValueKind == JsonValueKind.String && op.Value.GetString() is { } source && GameSources.All.Contains(source)
            ? source
            : throw Refuse(ErrorCodes.SettingInvalidValue, index, "'source' must be one of the game sources");

    private static SettingKey KeyFor(SettingsOp op, int index) =>
        Keys.TryGet(op.Key, out var key)
            ? key
            : throw Refuse(ErrorCodes.SettingUnknownKey, index, $"'{op.Key}' is not a settings key");

    private static JsonElement Normalize(SettingKey key, SettingsOp op, int index) =>
        key.TryNormalize(op.Value, out var normalized)
            ? normalized
            : throw Refuse(ErrorCodes.SettingInvalidValue, index, $"the value doesn't fit '{key.Name}'");

    private static ErrorCodeException Refuse(string code, int index, string reason) =>
        new(code, $"Settings op {index} refused: {reason}.");
}
