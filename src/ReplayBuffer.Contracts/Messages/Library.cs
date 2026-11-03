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
public sealed record LibraryItem(string Path, string? ProfileId, long Size, DateTimeOffset Date, bool BeingWritten);

/// <summary><c>library.itemRemoved</c>: the item at <see cref="Path"/> (relative to the Output Folder) left the Library.</summary>
public sealed record LibraryItemRemoved(string Path);

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

    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [ItemAdded, ItemChanged, ItemRemoved];

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
public sealed partial class LibraryJsonContext : JsonSerializerContext;
