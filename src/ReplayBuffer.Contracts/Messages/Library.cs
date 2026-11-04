using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// One file in the Library: a Clip (later a Recording) in the Output Folder.
/// </summary>
/// <param name="Path">The file's path relative to the Output Folder; the item's key (compared ordinal ignore-case).</param>
/// <param name="ProfileId">The Game Profile it was saved for, or <c>null</c> when unknown (a file the start scan found).</param>
/// <param name="Size">The file's size in bytes; 0 while it is being written.</param>
/// <param name="Date">When it was saved: the write's start, or the file's creation time for a scanned file.</param>
/// <param name="BeingWritten">Whether a writer is still writing it; such an item is shown locked and never persisted.</param>
/// <param name="LengthSeconds">Its length in seconds, read by FFmpeg's demuxer (story 7.3); <c>null</c> until read, or when it can't be read.</param>
/// <param name="Favourite">Whether it is a Favourite (story 7.4), set by <c>library.setFavourite</c>.</param>
/// <param name="Modified">The file's last write time (UTC) at <see cref="Size"/>; with the path and size it is the item's identity (AD-3). <c>null</c> while being written.</param>
public sealed record LibraryItem(
    string Path,
    string? ProfileId,
    long Size,
    DateTimeOffset Date,
    bool BeingWritten,
    double? LengthSeconds = null,
    bool Favourite = false,
    DateTimeOffset? Modified = null);

/// <summary>
/// <c>library.setFavourite</c>'s arguments: the item's identity as the App saw it (AD-3) and the
/// wanted flag. Refused with <c>library-item-changed</c> when the identity no longer matches.
/// </summary>
/// <param name="Path">The item's path relative to the Output Folder.</param>
/// <param name="Size">The item's <see cref="LibraryItem.Size"/>.</param>
/// <param name="Modified">The item's <see cref="LibraryItem.Modified"/>.</param>
/// <param name="Favourite">Whether it should be a Favourite.</param>
public sealed record LibrarySetFavourite(string Path, long Size, DateTimeOffset? Modified, bool Favourite);

/// <summary><c>library.itemRemoved</c>: the item at <see cref="Path"/> (relative to the Output Folder) left the Library.</summary>
public sealed record LibraryItemRemoved(string Path);

/// <summary><c>library.reset</c>: the Library is now exactly <see cref="Items"/>, e.g. after the Output Folder changed.</summary>
public sealed record LibraryReset(IReadOnlyList<LibraryItem> Items);

/// <summary><c>library.folderChanged</c>: the Library now indexes <see cref="Folder"/>, the absolute Output Folder its paths are relative to.</summary>
public sealed record LibraryFolderChanged(string Folder);

/// <summary>
/// <c>library.cleanupBlockedChanged</c>: whether Cleanup is held off because the index was rebuilt
/// from the Output Folder (a corrupt <c>library.json</c>) and that hasn't been acknowledged yet.
/// </summary>
public sealed record LibraryCleanupBlocked(bool Blocked);

/// <summary>
/// Library messages (epic-library-cleanup): the Library index and Cleanup.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// The LibraryActor is the only producer of the events (AD-17).
/// </summary>
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

    /// <summary>The Output Folder the Library indexes changed; published just before the <c>library.reset</c> that rebuilds it.</summary>
    public static EventType<LibraryFolderChanged> FolderChanged { get; } = new(
        "library.folderChanged",
        LibraryJsonContext.Default.LibraryFolderChanged,
        "libraryFolder",
        (snapshot, changed) => snapshot with { LibraryFolder = changed.Folder });

    /// <summary>Sets or clears an item's Favourite; it is published as <c>library.itemChanged</c>, never applied by the App first.</summary>
    public static CommandType<LibrarySetFavourite, Empty> SetFavourite { get; } =
        new("library.setFavourite", LibraryJsonContext.Default.LibrarySetFavourite, LibraryJsonContext.Default.Empty);

    public static IReadOnlyList<CommandType> Commands { get; } = [SetFavourite];

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
[JsonSerializable(typeof(Empty))]
public sealed partial class LibraryJsonContext : JsonSerializerContext;
