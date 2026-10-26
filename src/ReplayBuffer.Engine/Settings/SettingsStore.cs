using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Hotkeys;

namespace ReplayBuffer.Engine.Settings;

/// <summary>What <see cref="SettingsStore.Open"/> found: the store, and an error code if the file was unreadable.</summary>
public sealed record SettingsLoad(SettingsStore Store, string? LoadCode);

/// <summary>
/// The engine's settings owner (AD-4): the instance's <c>settings.json</c>, revision-checked
/// all-or-nothing updates, atomic writes. <see cref="Current"/> is an immutable document that is
/// swapped only after the file holds it.
/// </summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly string _tempPath;
    private SettingsDocument _current;

    private SettingsStore(string folder, SettingsDocument document)
    {
        Folder = folder;
        _path = Path.Combine(folder, FileName);
        _tempPath = _path + ".tmp";
        _current = document;
    }

    /// <summary>
    /// Raised after each commit, under the store's lock, with the new document. A handler's exception
    /// propagates out of <see cref="Update"/>, but the update has already committed.
    /// </summary>
    public event Action<SettingsDocument>? Changed;

    public string Folder { get; }

    public string FilePath => _path;

    /// <summary>The last committed document.</summary>
    public SettingsDocument Current => Volatile.Read(ref _current);

    /// <summary>Test seam: runs after the temp file is written and before it replaces the settings file.</summary>
    internal Action? BeforeReplace { get; set; }

    /// <summary>
    /// Opens the store for <paramref name="folder"/> (normally <see cref="Instance.RoamingDataFolder"/>).
    /// A missing file is the empty document; an unreadable one is moved aside and reported as
    /// <see cref="ErrorCodes.SettingsUnreadable"/>. A stale temp file from a killed write is deleted.
    /// </summary>
    public static SettingsLoad Open(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        folder = Path.GetFullPath(folder);
        var path = Path.Combine(folder, FileName);

        TryDelete(path + ".tmp");

        if (!File.Exists(path))
        {
            return new SettingsLoad(new SettingsStore(folder, SettingsDocument.Empty), null);
        }

        var document = TryParse(File.ReadAllBytes(path));
        if (document is not null)
        {
            return new SettingsLoad(new SettingsStore(folder, document), null);
        }

        MoveAside(path);
        return new SettingsLoad(new SettingsStore(folder, SettingsDocument.Empty), ErrorCodes.SettingsUnreadable);
    }

    /// <summary>
    /// Applies <paramref name="update"/> all-or-nothing: validates every op, writes the file, swaps
    /// <see cref="Current"/> and raises <see cref="Changed"/>. A refused update changes nothing.
    /// </summary>
    /// <returns>The committed document, one revision on.</returns>
    /// <exception cref="ErrorCodeException">The update was refused; <see cref="ErrorCodeException.Code"/> says why.</exception>
    /// <remarks>
    /// An exception thrown by a <see cref="Changed"/> handler propagates out of this method after the
    /// update has committed: the file and <see cref="Current"/> already hold the new document.
    /// </remarks>
    public SettingsDocument Update(SettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Ops);

        lock (_lock)
        {
            var current = _current;
            if (update.ExpectedRevision != current.Revision)
            {
                throw new ErrorCodeException(
                    ErrorCodes.SettingsConflict,
                    $"Settings are at revision {current.Revision}, not {update.ExpectedRevision}.");
            }

            var next = Apply(current, update.Ops) with { Revision = current.Revision + 1 };
            Write(next);
            Volatile.Write(ref _current, next);
            Changed?.Invoke(next);
            return next;
        }
    }

    private static SettingsDocument Apply(SettingsDocument document, IReadOnlyList<SettingsOp> ops)
    {
        var global = document.Global.ToBuilder();
        var profiles = document.Profiles.ToBuilder();

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
        return document with { Global = global.ToImmutable(), Profiles = profiles.ToImmutable() };
    }

    /// <summary>An update that sets <c>hotkeys</c> with two actions on one combination is refused (AD-11).</summary>
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

        if (op.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Refuse(ErrorCodes.SettingInvalidValue, index, $"'{op.Key}' must be true or false");
        }

        var value = op.Value.GetBoolean();
        return op.Key == ProfileAttributes.Ignored
            ? profile with { Ignored = value }
            : profile with { RecordingsEnabled = value };
    }

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

    private void Write(SettingsDocument document)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    JsonSerializer.Serialize(writer, document, SettingsJsonContext.Default.SettingsDocument);
                }

                stream.Flush(flushToDisk: true);
            }

            BeforeReplace?.Invoke();
            File.Move(_tempPath, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(_tempPath);
            throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, $"Settings could not be written to '{_path}': {e.Message}", e);
        }
    }

    private static SettingsDocument? TryParse(byte[] json)
    {
        try
        {
            var document = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDocument);
            if (document is null || document.SchemaVersion != SettingsDocument.CurrentSchemaVersion || document.Revision < 0
                || document.Profiles.Values.Any(p => p is null))
            {
                return null;
            }

            return SettingsMaps.WithOrdinalKeys(document);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void MoveAside(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{path}.unreadable-{stamp}";
        for (var n = 2; File.Exists(target); n++)
        {
            target = $"{path}.unreadable-{stamp}-{n}";
        }

        File.Move(path, target);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp file that can't be deleted is overwritten by the next write.
        }
    }
}
