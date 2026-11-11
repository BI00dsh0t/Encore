using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The Library's rows as the list shows them: grouped, filtered, sorted and searched. UI thread.</summary>
internal sealed class LibraryList : Observable
{
    private const string DayFormat = "yyyy-MM-dd";

    private readonly TimeProvider _time;
    private readonly LibraryThumbnails? _thumbnails;
    private readonly Func<string, LibraryRow> _newRow;
    private readonly Dictionary<string, LibraryRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibraryGroupHeader> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryGameOption> _gameOptions = new(StringComparer.Ordinal);
    private readonly LibraryGameOption _allGames = new(null) { Name = "All games" };
    private List<ListedItem> _items = [];
    private List<ListedItem> _readItems = [];
    private Dictionary<string, string> _readGames = [];
    private List<LibraryRow> _shownRows = [];
    private Dictionary<string, int> _shownIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasSnapshot;
    private LibraryGrouping _groupBy;
    private string? _gameFilter;
    private LibraryDateFilter _dateFilter;
    private bool _favouritesOnly;
    private LibrarySort _sort;
    private string _searchText = "";
    private RelayCommand? _clearSearchCommand;

    /// <summary>The view state is the remembered grouping, filters and sort; newRow makes the row of a new path.</summary>
    public LibraryList(LibraryViewState view, TimeProvider time, LibraryThumbnails? thumbnails, Func<string, LibraryRow> newRow)
    {
        _time = time;
        _thumbnails = thumbnails;
        _newRow = newRow;
        (_groupBy, _gameFilter, _dateFilter) = (view.GroupBy, view.Game, view.Date);
        (_favouritesOnly, _sort) = (view.FavouritesOnly, view.Sort);
        Games.Add(_allGames);
    }

    /// <summary>Raised after the shown rows were rebuilt.</summary>
    public event Action? Changed;

    /// <summary>Raised after the grouping, a remembered filter or the sort changed, so the choice can be saved.</summary>
    public event Action? ViewChanged;

    /// <summary>All the items, newest first, whatever the grouping and filters.</summary>
    public ObservableCollection<LibraryRow> Rows { get; } = [];

    /// <summary>What the list shows: each group's header followed by its rows, filtered.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>"All games" and every game present, in group order.</summary>
    public ObservableCollection<LibraryGameOption> Games { get; } = [];

    public IReadOnlyList<LibraryChoice<LibraryGrouping>> GroupOptions { get; } =
    [
        new(LibraryGrouping.Game, "Game"),
        new(LibraryGrouping.Day, "Day"),
        new(LibraryGrouping.None, "None"),
    ];

    public IReadOnlyList<LibraryChoice<LibrarySort>> SortOptions { get; } =
    [
        new(LibrarySort.NewestFirst, "Newest first"),
        new(LibrarySort.OldestFirst, "Oldest first"),
        new(LibrarySort.Name, "Name"),
        new(LibrarySort.LargestFirst, "Largest first"),
        new(LibrarySort.LongestFirst, "Longest first"),
        new(LibrarySort.Game, "Game"),
    ];

    public IReadOnlyList<LibraryChoice<LibraryDateFilter>> DateOptions { get; } =
    [
        new(LibraryDateFilter.All, "All dates"),
        new(LibraryDateFilter.Today, "Today"),
        new(LibraryDateFilter.Last7Days, "Last 7 days"),
        new(LibraryDateFilter.Last30Days, "Last 30 days"),
    ];

    public LibraryGrouping GroupBy
    {
        get => _groupBy;
        set => SetView(ref _groupBy, value);
    }

    /// <summary>The shown game's group key; null for all games.</summary>
    public string? GameFilter
    {
        get => _gameFilter;
        set
        {
            if (SetView(ref _gameFilter, value))
            {
                OnPropertyChanged(nameof(SelectedGame));
            }
        }
    }

    /// <summary>The game combo's selection; a null set (the combo's items changing) is ignored.</summary>
    public LibraryGameOption? SelectedGame
    {
        get => _gameFilter is null ? _allGames : _gameOptions.GetValueOrDefault(_gameFilter);
        set
        {
            if (value is not null)
            {
                GameFilter = value.Key;
            }
        }
    }

    public LibraryDateFilter DateFilter
    {
        get => _dateFilter;
        set => SetView(ref _dateFilter, value);
    }

    public bool FavouritesOnly
    {
        get => _favouritesOnly;
        set => SetView(ref _favouritesOnly, value);
    }

    /// <summary>How rows are ordered within each group; remembered with the other view choices.</summary>
    public LibrarySort Sort
    {
        get => _sort;
        set => SetView(ref _sort, value);
    }

    /// <summary>The search: rows whose file name contains it, ignoring case, on top of the other filters. Never remembered.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
            {
                OnPropertyChanged(nameof(IsSearching));
                _clearSearchCommand?.RaiseCanExecuteChanged();
                Apply();
            }
        }
    }

    public bool IsSearching => SearchText.Trim().Length > 0;

    /// <summary>Whether a filter or the search may be hiding items.</summary>
    public bool IsFiltered => GameFilter is not null || DateFilter != LibraryDateFilter.All || FavouritesOnly || IsSearching;

    /// <summary>The list area's message when a search matches nothing; null otherwise.</summary>
    public string? NoResultsText =>
        IsSearching && _hasSnapshot && _shownRows.Count == 0 ? $"No items match “{SearchText.Trim()}”." : null;

    /// <summary>Clears the search; the view clears its box directly, so Esc works within the binding delay.</summary>
    public ICommand ClearSearchCommand =>
        _clearSearchCommand ??= new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);

    /// <summary>The shown list's summary with nothing selected: "{n} items · {size}".</summary>
    public string SummaryText
    {
        get
        {
            var size = LibraryText.SizeText(_shownRows.Sum(row => row.Item?.Size ?? 0));
            return _shownRows.Count == 1
                ? $"1 item · {size}"
                : string.Create(CultureInfo.InvariantCulture, $"{_shownRows.Count} items · {size}");
        }
    }

    /// <summary>The rows the list shows, in shown order, without the group headers.</summary>
    public IReadOnlyList<LibraryRow> ShownRows => _shownRows;

    /// <summary>Every row of the Library, shown or hidden by a filter.</summary>
    public IEnumerable<LibraryRow> AllRows => _rows.Values;

    /// <summary>The grouping, filters and sort to remember, with the player's volume the caller adds.</summary>
    public LibraryViewState ViewState(double playerVolume, bool playerMuted) =>
        new(_groupBy, _gameFilter, _dateFilter, _favouritesOnly, _sort, playerVolume, playerMuted);

    /// <summary>Whether the Library holds an item at this path, shown or not.</summary>
    public bool Contains(string path) => _rows.ContainsKey(path);

    public LibraryRow? Find(string path) => _rows.GetValueOrDefault(path);

    public bool IsShown(string path) => _shownIndex.ContainsKey(path);

    /// <summary>The row's place among the shown rows; the path must be shown.</summary>
    public int ShownIndexOf(string path) => _shownIndex[path];

    /// <summary>Takes the snapshot's items into the rows. The list on screen changes only at <see cref="Publish"/>.</summary>
    public void Read(StateSnapshot state)
    {
        var ordered = state.Library
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _readItems = new List<ListedItem>(ordered.Count);
        _readGames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            if (!_rows.TryGetValue(item.Path, out var row))
            {
                row = _newRow(item.Path);
                _rows.Add(item.Path, row);
            }

            var (key, name) = LibraryGameKeys.Of(item, state.Settings);
            _readGames.TryAdd(key, name);
            row.Show(item, name);
            RequestThumbnail(row, item, state.LibraryFolder);
            _readItems.Add(new ListedItem(row, item, key));
        }

        ForgetRowsNotIn(ordered);
    }

    /// <summary>Shows what <see cref="Read"/> took, changing the bound lists in place.</summary>
    public void Publish()
    {
        CollectionSync.Apply(Rows, _readItems.ConvertAll(listed => listed.Row));
        _items = _readItems;
        _hasSnapshot = true;
        SyncGames(_readGames);
        Apply();
    }

    private void ForgetRowsNotIn(List<LibraryItem> items)
    {
        if (_rows.Count == items.Count)
        {
            return;
        }

        var paths = items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _rows.Keys.Where(path => !paths.Contains(path)).ToList())
        {
            _rows.Remove(gone);
        }
    }

    // A locked item is never asked for, and forgets its request, so it asks again once written.
    private void RequestThumbnail(LibraryRow row, LibraryItem item, string? folder)
    {
        if (item.BeingWritten)
        {
            row.ThumbnailRequest = null;
            row.Thumbnail = null;
            return;
        }

        if (_thumbnails is null || string.IsNullOrEmpty(folder))
        {
            return;
        }

        var path = System.IO.Path.Combine(folder, item.Path);
        if (row.ThumbnailRequest != (path, item.Size))
        {
            _thumbnails.Request(row, path, item.Size);
        }
    }

    private void SyncGames(Dictionary<string, string> games)
    {
        var wanted = new List<LibraryGameOption>(games.Count + 1) { _allGames };
        foreach (var (key, name) in games
            .OrderBy(game => LibraryGameKeys.Rank(game.Key))
            .ThenBy(game => game.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.Key, StringComparer.Ordinal))
        {
            if (!_gameOptions.TryGetValue(key, out var option))
            {
                option = new LibraryGameOption(key);
                _gameOptions.Add(key, option);
            }

            option.Name = name;
            wanted.Add(option);
        }

        foreach (var gone in _gameOptions.Keys.Where(key => !games.ContainsKey(key)).ToList())
        {
            _gameOptions.Remove(gone);
        }

        CollectionSync.Apply(Games, wanted);

        // A persisted filter survives the empty first render; a game gone from a real Library resets it.
        if (_gameFilter is not null && !_gameOptions.ContainsKey(_gameFilter) && games.Count > 0)
        {
            GameFilter = null;
        }
        else
        {
            OnPropertyChanged(nameof(SelectedGame));
        }
    }

    // Rebuilds Entries from the last snapshot under the current grouping and filters.
    private void Apply()
    {
        if (!_hasSnapshot)
        {
            return;
        }

        var today = _time.GetLocalNow().Date;
        var filtered = Filter(today);

        // The sort orders rows within each group; grouping keeps that order inside each group.
        var comparer = new LibraryRowComparer(_sort, GameName);
        filtered.Sort((a, b) => comparer.Compare((a.Listed.Item, a.Listed.Key), (b.Listed.Item, b.Listed.Key)));

        var wanted = Group(filtered, today);
        CollectionSync.Apply(Entries, wanted);
        IndexShownRows(wanted);
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(SummaryText));
        Changed?.Invoke();
    }

    // The items that pass the game, date, Favourite and search filters, each with its local day.
    private List<(ListedItem Listed, DateTime Day)> Filter(DateTime today)
    {
        var zone = _time.LocalTimeZone;
        var from = _dateFilter switch
        {
            LibraryDateFilter.Today => today,
            LibraryDateFilter.Last7Days => today.AddDays(-6),
            LibraryDateFilter.Last30Days => today.AddDays(-29),
            _ => DateTime.MinValue,
        };

        var search = SearchText.Trim();
        var filtered = new List<(ListedItem Listed, DateTime Day)>(_items.Count);
        foreach (var listed in _items)
        {
            var day = TimeZoneInfo.ConvertTime(listed.Item.Date, zone).Date;
            if ((_gameFilter is null || listed.Key == _gameFilter)
                && day >= from
                && (_dateFilter == LibraryDateFilter.All || day <= today)
                && (!_favouritesOnly || listed.Item.Favourite)
                && (search.Length == 0 || listed.Row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add((listed, day));
            }
        }

        return filtered;
    }

    // The entries in list order: each group's header, then its rows.
    private List<object> Group(List<(ListedItem Listed, DateTime Day)> filtered, DateTime today)
    {
        var wanted = new List<object>(filtered.Count + 16);
        switch (_groupBy)
        {
            case LibraryGrouping.Game:
                AddGameGroups(wanted, filtered);
                break;
            case LibraryGrouping.Day:
                AddDayGroups(wanted, filtered, today);
                break;
            default:
                wanted.AddRange(filtered.Select(entry => entry.Listed.Row));
                break;
        }

        return wanted;
    }

    // Games in the combo's order.
    private void AddGameGroups(List<object> wanted, List<(ListedItem Listed, DateTime Day)> filtered)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < Games.Count; i++)
        {
            order[Games[i].Key!] = i;
        }

        var groups = filtered.GroupBy(entry => entry.Listed.Key).OrderBy(group => order.GetValueOrDefault(group.Key, int.MaxValue));
        foreach (var group in groups)
        {
            wanted.Add(Header("game|" + group.Key, GameName(group.Key)));
            wanted.AddRange(group.Select(entry => entry.Listed.Row));
        }
    }

    // Days newest first, or oldest first under Oldest first.
    private void AddDayGroups(List<object> wanted, List<(ListedItem Listed, DateTime Day)> filtered, DateTime today)
    {
        var days = filtered.GroupBy(entry => entry.Day);
        var ordered = _sort == LibrarySort.OldestFirst ? days.OrderBy(group => group.Key) : days.OrderByDescending(group => group.Key);
        foreach (var group in ordered)
        {
            wanted.Add(Header("day|" + group.Key.ToString(DayFormat, CultureInfo.InvariantCulture), DayName(group.Key, today)));
            wanted.AddRange(group.Select(entry => entry.Listed.Row));
        }
    }

    private void IndexShownRows(List<object> entries)
    {
        _shownRows = entries.OfType<LibraryRow>().ToList();
        _shownIndex = new Dictionary<string, int>(_shownRows.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _shownRows.Count; i++)
        {
            _shownIndex[_shownRows[i].Path] = i;
        }
    }

    private string GameName(string key) => _gameOptions.TryGetValue(key, out var option) ? option.Name : key;

    private static string DayName(DateTime day, DateTime today)
    {
        if (day == today)
        {
            return "Today";
        }

        bool isYesterday = day == today.AddDays(-1);
        return isYesterday ? "Yesterday" : day.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    private LibraryGroupHeader Header(string key, string name)
    {
        if (!_headers.TryGetValue(key, out var header))
        {
            header = new LibraryGroupHeader(key);
            _headers.Add(key, header);
        }

        header.Name = name;
        return header;
    }

    private bool SetView<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name))
        {
            return false;
        }

        Apply();
        ViewChanged?.Invoke();
        return true;
    }

    /// <summary>One item of the last snapshot: its row, the item and its game's group key.</summary>
    private readonly record struct ListedItem(LibraryRow Row, LibraryItem Item, string Key);
}
