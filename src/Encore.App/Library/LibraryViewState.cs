using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.App.Library;

/// <summary>How the Library list is grouped.</summary>
internal enum LibraryGrouping
{
    Game,
    Day,

    /// <summary>One flat list with no headers.</summary>
    None,
}

/// <summary>How the Library orders its rows within each group.</summary>
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

/// <summary>The Library's view choices, remembered per instance in a small JSON file; any read or write failure is ignored.</summary>
internal sealed record LibraryViewState(
    LibraryGrouping GroupBy = LibraryGrouping.Game,
    string? Game = null,
    LibraryDateFilter Date = LibraryDateFilter.All,
    bool FavouritesOnly = false,
    LibrarySort Sort = LibrarySort.NewestFirst,
    double PlayerVolume = 1.0,
    bool PlayerMuted = false)
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
            return state is not null && Enum.IsDefined(state.GroupBy) && Enum.IsDefined(state.Date) && Enum.IsDefined(state.Sort)
                ? state with { PlayerVolume = double.IsFinite(state.PlayerVolume) ? Math.Clamp(state.PlayerVolume, 0, 1) : 1.0 }
                : Default;
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
