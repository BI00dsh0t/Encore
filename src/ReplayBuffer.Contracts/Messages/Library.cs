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

/// <summary>
/// <c>library.rename</c>'s arguments (story 7.7): the item's identity as the App saw it (AD-3) and its
/// new base name. The engine appends <c>.mp4</c> and keeps the folder.
/// </summary>
/// <param name="Path">The item's path relative to the Output Folder.</param>
/// <param name="Size">The item's <see cref="LibraryItem.Size"/>.</param>
/// <param name="Modified">The item's <see cref="LibraryItem.Modified"/>.</param>
/// <param name="NewName">The new file name without its folder or the <c>.mp4</c> extension.</param>
public sealed record LibraryRename(string Path, long Size, DateTimeOffset? Modified, string NewName);

/// <summary>
/// <c>library.delete</c>'s arguments (story 7.7): the item's identity as the App saw it (AD-3). The file
/// is moved to the Recycle Bin, never deleted permanently.
/// </summary>
/// <param name="Path">The item's path relative to the Output Folder.</param>
/// <param name="Size">The item's <see cref="LibraryItem.Size"/>.</param>
/// <param name="Modified">The item's <see cref="LibraryItem.Modified"/>.</param>
public sealed record LibraryDelete(string Path, long Size, DateTimeOffset? Modified);

/// <summary><c>library.itemRemoved</c>: the item at <see cref="Path"/> (relative to the Output Folder) left the Library.</summary>
/// <param name="Path">The item's path relative to the Output Folder.</param>
/// <param name="Reason">Why it left, when the engine removed it itself: one of <see cref="LibraryRemovalReasons"/>; <c>null</c> otherwise.</param>
public sealed record LibraryItemRemoved(string Path, string? Reason = null);

/// <summary>The <see cref="LibraryItemRemoved.Reason"/> values.</summary>
public static class LibraryRemovalReasons
{
    /// <summary>Cleanup (story 7.8) deleted the file permanently to keep the Output Folder within its limits.</summary>
    public const string Cleanup = "cleanup";
}

/// <summary>
/// <c>cleanup.preview</c>'s arguments (story 7.8): the limits to plan with, each null when off, like the
/// <c>cleanup</c> setting. Nothing is deleted.
/// </summary>
/// <param name="MaxBytes">The Output Folder's size limit in bytes, or <c>null</c>.</param>
/// <param name="MaxAgeSeconds">The age limit in seconds, or <c>null</c>.</param>
public sealed record LibraryCleanupPreview(long? MaxBytes, long? MaxAgeSeconds);

/// <summary><c>cleanup.preview</c>'s result: what a Cleanup pass with those limits would delete now, oldest first.</summary>
/// <param name="Count">How many files it would delete.</param>
/// <param name="Bytes">Their total size in bytes.</param>
/// <param name="Paths">Their paths relative to the Output Folder, in the order they would be deleted.</param>
public sealed record LibraryCleanupPlan(int Count, long Bytes, IReadOnlyList<string> Paths);

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

    /// <summary>
    /// Renames an item's file in its folder; it is published as <c>library.itemRemoved</c> and
    /// <c>library.itemAdded</c>, keeping the item's metadata. Refused while the item is being written.
    /// </summary>
    public static CommandType<LibraryRename, Empty> Rename { get; } =
        new("library.rename", LibraryJsonContext.Default.LibraryRename, LibraryJsonContext.Default.Empty);

    /// <summary>
    /// Moves an item's file to the Recycle Bin; it is published as <c>library.itemRemoved</c>. Refused
    /// while the item is being written, or when its volume has no Recycle Bin.
    /// </summary>
    public static CommandType<LibraryDelete, Empty> Delete { get; } =
        new("library.delete", LibraryJsonContext.Default.LibraryDelete, LibraryJsonContext.Default.Empty);

    /// <summary>
    /// What Cleanup would delete now with the given limits (story 7.8), ignoring whether it may run yet;
    /// nothing is deleted.
    /// </summary>
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
