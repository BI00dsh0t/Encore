using System.Globalization;
using System.Text.Json;
using Encore.Contracts;
using Encore.Contracts.Settings;

namespace Encore.Engine.Settings;

/// <summary>The engine's settings owner: <c>settings.json</c>, revision-checked all-or-nothing updates, atomic writes.</summary>
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

    /// <summary>Raised after each commit, under the store's lock; a handler's exception propagates, but the commit stands.</summary>
    public event Action<SettingsDocument>? Changed;

    public string Folder { get; }

    public string FilePath => _path;

    /// <summary>The last committed document.</summary>
    public SettingsDocument Current => Volatile.Read(ref _current);

    /// <summary>The folder defaults for the no-nesting rule; null checks nothing. The engine host sets it.</summary>
    public (string Cache, string Clips)? FolderDefaults { get; set; }

    /// <summary>Test seam: runs after the temp file is written and before it replaces the settings file.</summary>
    internal Action? BeforeReplace { get; set; }

    /// <summary>Opens the store for a folder; a missing file is the empty document, an unreadable one is moved aside.</summary>
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

    /// <summary>Reads the document without opening a store or changing the folder; throws when the file can't be read.</summary>
    public static SettingsDocument ReadDocument(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var path = Path.Combine(Path.GetFullPath(folder), FileName);
        byte[] json;
        try
        {
            json = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return SettingsDocument.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ErrorCodeException(ErrorCodes.SettingsUnreadable, $"Settings could not be read from '{path}': {e.Message}", e);
        }

        return TryParse(json)
            ?? throw new ErrorCodeException(ErrorCodes.SettingsUnreadable, $"'{path}' is not a readable settings document.");
    }

    /// <summary>Applies an update all-or-nothing and returns the committed document; a refused update throws, changing nothing.</summary>
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

            var next = SettingsOps.Apply(current, update.Ops) with { Revision = current.Revision + 1 };
            if (FolderDefaults is { } defaults
                && update.Ops.Any(op => op.Scope == SettingsScope.Global && (op.Key == Keys.RingPath.Name || op.Key == Keys.OutputFolder.Name))
                && RingPathRule.Nests(next, defaults.Cache, defaults.Clips))
            {
                throw new ErrorCodeException(ErrorCodes.RingPathInvalid, RingPathRule.Refusal);
            }

            Write(next);
            Volatile.Write(ref _current, next);
            Changed?.Invoke(next);
            return next;
        }
    }

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
