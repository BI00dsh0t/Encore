using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;

namespace Encore.Contracts.Messages;

/// <summary>One file in the Library: a Clip or a Recording in the Output Folder. Fields: docs/code-notes.md.</summary>
public sealed record LibraryItem(
    string Path,
    string? ProfileId,
    long Size,
    DateTimeOffset Date,
    bool BeingWritten,
    double? LengthSeconds = null,
    bool Favourite = false,
    DateTimeOffset? Modified = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

/// <summary>The <see cref="LibraryItem.Kind"/> values, set by the engine when a writer begins; a scanned file has none.</summary>
public static class LibraryItemKinds
{
    public const string Clip = "clip";
    public const string Recording = "recording";
    public const string MonitorRecording = "monitorRecording";
}

/// <summary><c>library.setFavourite</c>'s arguments: the item's identity as the App saw it, and the wanted flag.</summary>
public sealed record LibrarySetFavourite(string Path, long Size, DateTimeOffset? Modified, bool Favourite);

/// <summary><c>library.rename</c>'s arguments: the item's identity and a new base name, without folder or extension.</summary>
public sealed record LibraryRename(string Path, long Size, DateTimeOffset? Modified, string NewName);

/// <summary><c>library.delete</c>'s arguments: the item's identity; the file goes to the Recycle Bin, never deleted for good.</summary>
public sealed record LibraryDelete(string Path, long Size, DateTimeOffset? Modified);

/// <summary><c>library.itemRemoved</c>: the item at this relative path left the Library; Reason is set if the engine removed it.</summary>
public sealed record LibraryItemRemoved(string Path, string? Reason = null);

/// <summary>The <see cref="LibraryItemRemoved.Reason"/> values.</summary>
public static class LibraryRemovalReasons
{
    /// <summary>Cleanup deleted the file permanently to keep the Output Folder within its limits.</summary>
    public const string Cleanup = "cleanup";
}

/// <summary><c>cleanup.preview</c>'s arguments: the limits to plan with, each null when off; nothing is deleted.</summary>
public sealed record LibraryCleanupPreview(long? MaxBytes, long? MaxAgeSeconds);

/// <summary><c>cleanup.preview</c>'s result: what a Cleanup pass with those limits would delete now, oldest first.</summary>
public sealed record LibraryCleanupPlan(int Count, long Bytes, IReadOnlyList<string> Paths);

/// <summary><c>library.reset</c>: the Library is now exactly <see cref="Items"/>, e.g. after the Output Folder changed.</summary>
public sealed record LibraryReset(IReadOnlyList<LibraryItem> Items);

/// <summary><c>library.folderChanged</c>: the Library now indexes this absolute Output Folder.</summary>
public sealed record LibraryFolderChanged(string Folder);

/// <summary><c>library.cleanupBlockedChanged</c>: whether Cleanup is held off after the index was rebuilt from the folder.</summary>
public sealed record LibraryCleanupBlocked(bool Blocked);

/// <summary>Library messages: the Library index and Cleanup; the LibraryActor is the only producer of the events.</summary>
public static class LibraryMessages
{
    /// <summary>An item joined the Library; it replaces any item with the same path.</summary>
    public static EventType<LibraryItem> ItemAdded { get; } = new(
        "library.itemAdded",
        LibraryJsonContext.Default.LibraryItem,
        "library",
        (snapshot, item) => snapshot with { Library = [.. Without(snapshot.Library, item.Path), item] });

    /// <summary>An item changed, e.g. its write ended; it replaces the item with the same path, or is appended.</summary>
    public static EventType<LibraryItem> ItemChanged { get; } = new(
        "library.itemChanged",
        LibraryJsonContext.Default.LibraryItem,
        "library",
        (snapshot, item) => snapshot with { Library = Replace(snapshot.Library, item) });

    /// <summary>An item left the Library.</summary>
    public static EventType<LibraryItemRemoved> ItemRemoved { get; } = new(
        "library.itemRemoved",
        LibraryJsonContext.Default.LibraryItemRemoved,
        "library",
        (snapshot, removed) => snapshot with { Library = [.. Without(snapshot.Library, removed.Path)] });

    /// <summary>The whole Library was rebuilt; its items replace the list.</summary>
    public static EventType<LibraryReset> Reset { get; } = new(
        "library.reset",
        LibraryJsonContext.Default.LibraryReset,
        "library",
        (snapshot, reset) => snapshot with { Library = [.. reset.Items] });

    /// <summary>Cleanup was blocked by a rebuilt index, or the rebuild was acknowledged.</summary>
    public static EventType<LibraryCleanupBlocked> CleanupBlockedChanged { get; } = new(
        "library.cleanupBlockedChanged",
        LibraryJsonContext.Default.LibraryCleanupBlocked,
        "cleanupBlocked",
        (snapshot, blocked) => snapshot with { CleanupBlocked = blocked.Blocked });

    /// <summary>The Output Folder changed; published just before the <c>library.reset</c> that rebuilds the Library.</summary>
    public static EventType<LibraryFolderChanged> FolderChanged { get; } = new(
        "library.folderChanged",
        LibraryJsonContext.Default.LibraryFolderChanged,
        "libraryFolder",
        (snapshot, changed) => snapshot with { LibraryFolder = changed.Folder });

    /// <summary>Sets or clears an item's Favourite; the change comes back as <c>library.itemChanged</c>.</summary>
    public static CommandType<LibrarySetFavourite, Empty> SetFavourite { get; } =
        new("library.setFavourite", LibraryJsonContext.Default.LibrarySetFavourite, LibraryJsonContext.Default.Empty);

    /// <summary>Renames an item's file in its folder, keeping its metadata; refused while the item is being written.</summary>
    public static CommandType<LibraryRename, Empty> Rename { get; } =
        new("library.rename", LibraryJsonContext.Default.LibraryRename, LibraryJsonContext.Default.Empty);

    /// <summary>Moves an item's file to the Recycle Bin; refused while it is being written or when there is no Recycle Bin.</summary>
    public static CommandType<LibraryDelete, Empty> Delete { get; } =
        new("library.delete", LibraryJsonContext.Default.LibraryDelete, LibraryJsonContext.Default.Empty);

    /// <summary>What Cleanup would delete now with the given limits, ignoring whether it may run yet; nothing is deleted.</summary>
    public static CommandType<LibraryCleanupPreview, LibraryCleanupPlan> CleanupPreview { get; } =
        new("cleanup.preview", LibraryJsonContext.Default.LibraryCleanupPreview, LibraryJsonContext.Default.LibraryCleanupPlan);

    public static IReadOnlyList<CommandType> Commands { get; } = [SetFavourite, Rename, Delete, CleanupPreview];

    public static IReadOnlyList<EventType> Events { get; } = [ItemAdded, ItemChanged, ItemRemoved, Reset, CleanupBlockedChanged, FolderChanged];

    private static IEnumerable<LibraryItem> Without(IReadOnlyList<LibraryItem>? items, string path) =>
        (items ?? []).Where(i => !string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    private static List<LibraryItem> Replace(IReadOnlyList<LibraryItem>? items, LibraryItem item)
    {
        var list = new List<LibraryItem>(items ?? []);
        var index = list.FindIndex(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            list.Add(item);
        }
        else
        {
            list[index] = item;
        }

        return list;
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LibraryItem))]
[JsonSerializable(typeof(LibraryItemRemoved))]
[JsonSerializable(typeof(LibraryReset))]
[JsonSerializable(typeof(LibraryCleanupBlocked))]
[JsonSerializable(typeof(LibraryFolderChanged))]
[JsonSerializable(typeof(LibrarySetFavourite))]
[JsonSerializable(typeof(LibraryRename))]
[JsonSerializable(typeof(LibraryDelete))]
[JsonSerializable(typeof(LibraryCleanupPreview))]
[JsonSerializable(typeof(LibraryCleanupPlan))]
[JsonSerializable(typeof(Empty))]
public sealed partial class LibraryJsonContext : JsonSerializerContext;
