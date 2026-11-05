using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayBuffer.App.Library;

/// <summary>How the Library list is grouped.</summary>
internal enum LibraryGrouping
{
    Game,
    Day,

    /// <summary>One flat list with no headers (story 10.3).</summary>
    None,
}

/// <summary>How the Library orders its rows within each group (story 10.3).</summary>
internal enum LibrarySort
{
    NewestFirst,
    OldestFirst,
    Name,
    LargestFirst,
    LongestFirst,
    Game,
}

/// <summary>Which dates the Library list shows, against the local today.</summary>
internal enum LibraryDateFilter
{
    All,
    Today,
    Last7Days,
    Last30Days,
}

/// <summary>
/// The Library's view choices (story 7.5), remembered per instance in a small JSON file. Any failure
/// to read or write the file is ignored: a load gives the defaults, a save is dropped. Story 10.3 adds
/// the sort; a file from before it has none and loads with Newest first. The search text isn't kept.
/// </summary>
/// <param name="Game">The game filter's group key; null for all games.</param>
internal sealed record LibraryViewState(
    LibraryGrouping GroupBy = LibraryGrouping.Game,
    string? Game = null,
    LibraryDateFilter Date = LibraryDateFilter.All,
    bool FavouritesOnly = false,
    LibrarySort Sort = LibrarySort.NewestFirst)
{
    private static LibraryViewState Default { get; } = new();

    public static LibraryViewState Load(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Default;
        }

        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), LibraryViewStateJson.Default.LibraryViewState);
            return state is not null && Enum.IsDefined(state.GroupBy) && Enum.IsDefined(state.Date) && Enum.IsDefined(state.Sort) ? state : Default;
        }
        catch (Exception)
        {
            // Missing, unreadable or unparsable: the defaults.
            return Default;
        }
    }

    public void Save(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, LibraryViewStateJson.Default.LibraryViewState));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Unwritable: the choice lasts only this run.
        }
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(LibraryViewState))]
internal sealed partial class LibraryViewStateJson : JsonSerializerContext;
