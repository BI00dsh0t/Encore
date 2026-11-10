using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Encore.App.Engine;
using Encore.App.Settings;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Library;

/// <summary>The Library page: the snapshot's items with selection, filters, sort and actions. UI thread; docs/code-notes.md.</summary>
internal sealed class LibraryPage : Observable
{
    /// <summary>Core's <c>ProfileFolders.MonitorRecordings</c>; the App doesn't reference Core.</summary>
    private const string MonitorRecordings = "Monitor Recordings";

    private const string OtherKey = "other";
    private const string DayFormat = "yyyy-MM-dd";
    private const int MaxExplorerWindows = 3;
    // A delete of more items than this shows its progress in the command bar.
    private const int ProgressFrom = 10;
    private static readonly string MonitorRecordingsKey = FolderKey(MonitorRecordings);

    private readonly IEngineSession _engine;
    private readonly LibraryThumbnails? _thumbnails;
    private readonly string? _viewStateFile;
    private readonly TimeProvider _time;
    private readonly ILibraryShell _shell;
    private readonly Func<string, bool> _confirmDelete;
    private readonly Func<string?> _pickFolder;
    private readonly Func<IPlayer> _newPlayer;
    private readonly Func<string, Stream>? _openRead;
    private readonly Action<string> _log;
    private PlayerPage? _player;
    private ClipCreatorPage? _creator;
    private LibraryItem? _creatorItem;
    private readonly IClipMediaService _clipMedia;
    private readonly string? _clipThumbnailCacheFolder;
    private double _playerVolume = 1.0;
    private bool _playerMuted;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    // The App's own renames in flight: old path → the path the item gets once the snapshot shows it.
    private readonly Dictionary<string, string> _renames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RelayCommand> _selectionCommands = [];
    private readonly Dictionary<string, LibraryRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibraryGroupHeader> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryGameOption> _gameOptions = new(StringComparer.Ordinal);
    private readonly LibraryGameOption _allGames = new(null) { Name = "All games" };
    private List<LibraryRow> _shownRows = [];
    private Dictionary<string, int> _shownIndex = new(StringComparer.OrdinalIgnoreCase);
    private List<LibraryRow> _shownSelected = [];
    private string? _anchor;
    private string? _focus;
    private bool _deleting;
    private int _deleteDone;
    private int _deleteTotal;
    private List<(LibraryRow Row, LibraryItem Item, string Key)> _shown = [];
    private bool _hasSnapshot;
    private FrameworkElement? _view;
    private bool _isCleanupBlocked;
    private string? _corruptWarningId;
    private LibraryGrouping _groupBy;
    private string? _gameFilter;
    private LibraryDateFilter _dateFilter;
    private bool _favouritesOnly;
    private LibrarySort _sort;
    private string _searchText = "";
    private RelayCommand? _clearSearchCommand;
    private string? _libraryFolder;
    private int _itemCount;
    private string? _actionError;
    private string? _actionErrorPath;

    /// <summary>Each optional argument replaces a real dependency (thumbnails, shell, prompts, player) for tests.</summary>
    public LibraryPage(
        IEngineSession engine,
        LibraryThumbnails? thumbnails = null,
        string? viewStateFile = null,
        TimeProvider? time = null,
        ILibraryShell? shell = null,
        Func<string, bool>? confirmDelete = null,
        Func<string?>? pickFolder = null,
        Func<IPlayer>? player = null,
        Func<string, Stream>? openRead = null,
        Action<string>? log = null,
        IClipMediaService? clipMedia = null,
        string? clipThumbnailCacheFolder = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        PropertyChanged += (_, e) =>
        {
            // The welcome card follows the filters and search.
            if (e.PropertyName is nameof(GameFilter) or nameof(DateFilter) or nameof(FavouritesOnly) or nameof(SearchText))
            {
                OnPropertyChanged(nameof(ShowsWelcome));
            }
        };
        _thumbnails = thumbnails;
        _viewStateFile = viewStateFile;
        _time = time ?? TimeProvider.System;
        _shell = shell ?? new WindowsLibraryShell();
        _confirmDelete = confirmDelete ?? ConfirmDelete;
        _pickFolder = pickFolder ?? PickFolder;
        _newPlayer = player ?? (() => new WindowsMediaPlayer());
        _openRead = openRead;
        _log = log ?? (_ => { });
        _clipMedia = clipMedia ?? new ClipMediaService();
        _clipThumbnailCacheFolder = clipThumbnailCacheFolder;
        DrillIn = new DrillIn(MainViewModel.LibraryTitle);
        DrillIn.Closed += _ => { ClosePlayer(); CloseCreator(); };
        AcknowledgeCommand = new RelayCommand(() => _ = AcknowledgeAsync());
        OpenCommand = Selection(OpenSelection, () => _shownSelected.Count == 1);
        PlayCommand = Selection(PlaySelection, () => _shownSelected is [{ IsLocked: false }]);
        CreateClipCommand = Selection(CreateClipSelection, () => _shownSelected is [{ IsLocked: false }]);
        FavouriteSelectionCommand = Selection(() => _ = FavouriteSelectionAsync(), () => _shownSelected.Count > 0);
        CopyCommand = Selection(CopySelection, () => _shownSelected.Any(r => !r.IsLocked));
        CopyToFolderCommand = Selection(CopySelectionToFolder, () => _shownSelected.Any(r => !r.IsLocked));
        ShowInExplorerCommand = Selection(ShowSelectionInExplorer, () => _shownSelected.Count > 0);
        RenameCommand = Selection(RenameSelection, () => _shownSelected is [{ IsLocked: false }]);
        DeleteCommand = Selection(DeleteSelection, () => _shownSelected.Any(IsDeletable));
        ClearSelectionCommand = Selection(ClearSelection, () => _selected.Count > 0);
        Games.Add(_allGames);

        var view = LibraryViewState.Load(viewStateFile);
        (_groupBy, _gameFilter, _dateFilter, _favouritesOnly, _sort) = (view.GroupBy, view.Game, view.Date, view.FavouritesOnly, view.Sort);
        (_playerVolume, _playerMuted) = (view.PlayerVolume, view.PlayerMuted);
    }

    /// <summary>Whether the banner shows: the index was rebuilt and Cleanup is off until acknowledged.</summary>
    public bool IsCleanupBlocked
    {
        get => _isCleanupBlocked;
        private set => Set(ref _isCleanupBlocked, value);
    }

    /// <summary>The last row action's failure, shown under the filters; null once one succeeds or its row leaves the Library.</summary>
    public string? ActionError
    {
        get => _actionError;
        private set
        {
            if (Set(ref _actionError, value))
            {
                OnPropertyChanged(nameof(HasActionError));
            }
        }
    }

    public bool HasActionError => ActionError is not null;

    /// <summary>Sends <c>warning.dismiss</c> for the active <c>library-corrupt</c> warning.</summary>
    public ICommand AcknowledgeCommand { get; }

    /// <summary>Whether any item is selected, shown or hidden by a filter: the command bar shows its actions.</summary>
    public bool HasSelection => _selected.Count > 0;

    /// <summary>Whether any selected item is shown; the actions act only on those.</summary>
    public bool HasShownSelection => _shownSelected.Count > 0;

    /// <summary>"{n} selected" for the selected items still in the Library; "Deleting {k} of {n}…" during a large delete.</summary>
    public string SelectionText => _deleting && _deleteTotal > ProgressFrom
        ? string.Create(CultureInfo.InvariantCulture, $"Deleting {_deleteDone} of {_deleteTotal}…")
        : string.Create(CultureInfo.InvariantCulture, $"{_selected.Count} selected");

    /// <summary>"({m} hidden by filters)" when a filter hides some selected items; else empty.</summary>
    public string HiddenText => _selected.Count - _shownSelected.Count is var hidden and > 0
        ? string.Create(CultureInfo.InvariantCulture, $"({hidden} hidden by filters)")
        : "";

    /// <summary>The shown list's summary with nothing selected: "{n} items · {size}".</summary>
    public string SummaryText
    {
        get
        {
            var size = SizeText(_shownRows.Sum(r => r.Item?.Size ?? 0));
            return _shownRows.Count == 1
                ? $"1 item · {size}"
                : string.Create(CultureInfo.InvariantCulture, $"{_shownRows.Count} items · {size}");
        }
    }

    /// <summary>"Remove from Favourites" when every shown selected item is one; else "Favourite".</summary>
    public string FavouriteLabel =>
        _shownSelected.Count > 0 && _shownSelected.All(r => r.IsFavourite) ? "Remove from Favourites" : "Favourite";

    /// <summary>Whether exactly one selected item is shown: Open and Rename show.</summary>
    public bool IsSingle => _shownSelected.Count == 1;

    /// <summary>Why Delete is disabled, when every shown selected item is a Favourite or being written; else null.</summary>
    public string? DeleteToolTip =>
        _shownSelected.Count > 0 && !_shownSelected.Any(IsDeletable) ? NotDeletableText(_shownSelected) : null;

    /// <summary>The focused row, when it is shown.</summary>
    public LibraryRow? FocusedRow => _focus is not null && _shownIndex.TryGetValue(_focus, out var i) ? _shownRows[i] : null;

    /// <summary>"Open in default player": hands the one selected item to the shell's default player, even while being written.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>"Play": plays the one shown selected item on the in-app player page, once it's written.</summary>
    public ICommand PlayCommand { get; }
    public ICommand CreateClipCommand { get; }
    public ClipCreatorPage? Creator { get => _creator; private set => Set(ref _creator, value); }

    /// <summary>The in-app player's drill-in: the Library's item page, with the breadcrumb, keys and close.</summary>
    public DrillIn DrillIn { get; }

    /// <summary>The open player page, or null on the list.</summary>
    public PlayerPage? Player
    {
        get => _player;
        private set => Set(ref _player, value);
    }

    /// <summary>Stars every shown selected item, or unstars them all when all are Favourites; one send per item that changes.</summary>
    public ICommand FavouriteSelectionCommand { get; }

    /// <summary>Puts the shown selected items that are written on the clipboard as one file drop.</summary>
    public ICommand CopyCommand { get; }

    /// <summary>Copies the shown selected items that are written into a picked folder, through the shell.</summary>
    public ICommand CopyToFolderCommand { get; }

    /// <summary>Shows the shown selected items selected in Explorer, one window per folder.</summary>
    public ICommand ShowInExplorerCommand { get; }

    /// <summary>Starts the inline rename of the one shown selected item; disabled while it is being written.</summary>
    public ICommand RenameCommand { get; }

    /// <summary>Moves the selected items that aren't Favourites or being written to the Recycle Bin, after one confirmation.</summary>
    public ICommand DeleteCommand { get; }

    public ICommand ClearSelectionCommand { get; }

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

    /// <summary>The empty Library's welcome card.</summary>
    public LibraryWelcome Welcome { get; } = new();

    /// <summary>Whether the welcome card shows: the Library holds nothing, with no filter or search.</summary>
    public bool ShowsWelcome =>
        _itemCount == 0 && GameFilter is null && DateFilter == LibraryDateFilter.All && !FavouritesOnly && !IsSearching;

    /// <summary>The list area's message when a search matches nothing; null otherwise.</summary>
    public string? NoResultsText => IsSearching && _hasSnapshot && _shownRows.Count == 0 ? $"No items match “{SearchText.Trim()}”." : null;

    /// <summary>Clears the search; the view clears its box directly, so Esc works within the binding delay.</summary>
    public ICommand ClearSearchCommand => _clearSearchCommand ??= new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);

    public FrameworkElement View => _view ??= new LibraryView { DataContext = this };

    /// <summary>Shows <paramref name="state"/>'s Library, changing the rows in place.</summary>
    public void Render(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Welcome.Show(state);
        if (_itemCount != state.Library.Count)
        {
            _itemCount = state.Library.Count;
            OnPropertyChanged(nameof(ShowsWelcome));
        }

        IsCleanupBlocked = state.CleanupBlocked;
        _libraryFolder = state.LibraryFolder;
        _corruptWarningId = state.Warnings.FirstOrDefault(w => w.Code == ErrorCodes.LibraryCorrupt)?.Id;
        var ordered = state.Library
            .OrderByDescending(i => i.Date)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = new List<(LibraryRow Row, LibraryItem Item, string Key)>(ordered.Count);
        var games = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            if (!_rows.TryGetValue(item.Path, out var row))
            {
                row = new LibraryRow(item.Path, r => _ = SetFavouriteAsync(r), (r, name) => _ = RenameAsync(r, name));
                _rows.Add(item.Path, row);
            }

            var (key, name) = GameKeyOf(item, state.Settings);
            games.TryAdd(key, name);
            row.Show(item, name);
            RequestThumbnail(row, item, state.LibraryFolder);
            shown.Add((row, item, key));
        }

        if (_rows.Count != ordered.Count)
        {
            var keys = ordered.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _rows.Keys.Where(k => !keys.Contains(k)).ToList())
            {
                _rows.Remove(gone);
            }
        }

        if (_actionErrorPath is { } failed && !_rows.ContainsKey(failed))
        {
            ClearActionError();
        }

        // The selection follows the App's own renames, and loses what left the Library; a hidden item stays.
        CarryRenames();
        _selected.RemoveWhere(p => !_rows.ContainsKey(p));
        if (_anchor is not null && !_rows.ContainsKey(_anchor))
        {
            _anchor = null;
        }

        // A player page whose item left the Library (deleted, renamed or cleaned up) closes and releases the file.
        if (DrillIn.ItemId is { } playing && !_rows.ContainsKey(playing))
        {
            DrillIn.Close();
        }
        if (_creatorItem is { } original && (!_rows.TryGetValue(original.Path, out var sourceRow)
            || sourceRow.Item is not { BeingWritten: false } current || current.Size != original.Size || current.Modified != original.Modified
            || Creator is { } creator && (state.LibraryFolder is null || !string.Equals(Path.GetFullPath(Path.Combine(state.LibraryFolder, original.Path)), creator.Source.Path, StringComparison.OrdinalIgnoreCase))))
        {
            DrillIn.Close();
        }

        var lostFocus = _focus is not null && !_rows.ContainsKey(_focus) ? _focus : null;
        var previous = _shownRows;

        Sync(Rows, shown.ConvertAll(s => s.Row));
        _shown = shown;
        _hasSnapshot = true;
        SyncGames(games);
        Apply();

        if (lostFocus is not null)
        {
            _focus = NextFocus(previous, lostFocus);
            RefreshSelection();
        }
    }

    // A pending rename whose new path has arrived moves the selection, anchor and focus there.
    private void CarryRenames()
    {
        foreach (var (from, to) in _renames.ToList())
        {
            if (_rows.ContainsKey(from))
            {
                continue;
            }

            _renames.Remove(from);
            if (!_rows.TryGetValue(to, out var renamed))
            {
                continue;
            }

            if (_selected.Remove(from))
            {
                _selected.Add(renamed.Path);
            }

            _anchor = Same(_anchor, from) ? renamed.Path : _anchor;
            _focus = Same(_focus, from) ? renamed.Path : _focus;
        }
    }

    // The next row of the previous shown order that is still shown, else the previous one.
    private string? NextFocus(List<LibraryRow> previous, string lost)
    {
        var index = previous.FindIndex(r => Same(r.Path, lost));
        if (index < 0)
        {
            return null;
        }

        for (var i = index + 1; i < previous.Count; i++)
        {
            if (_shownIndex.ContainsKey(previous[i].Path))
            {
                return previous[i].Path;
            }
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (_shownIndex.ContainsKey(previous[i].Path))
            {
                return previous[i].Path;
            }
        }

        return null;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The item's game, as group key and header name: its profile's name, else its top-level folder's, else "Other".</summary>
    private static (string Key, string Name) GameKeyOf(LibraryItem item, SettingsDocument settings)
    {
        if (item.ProfileId is { } id && settings.Profiles.TryGetValue(id, out var profile))
        {
            return (ProfileKey(id), profile.Name);
        }

        var separator = item.Path.IndexOfAny(['\\', '/']);
        if (separator <= 0)
        {
            return (OtherKey, "Other");
        }

        var folder = item.Path[..separator];
        foreach (var (profileId, candidate) in settings.Profiles)
        {
            if (string.Equals(candidate.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                return (ProfileKey(profileId), candidate.Name);
            }
        }

        return (FolderKey(folder), folder);
    }

    private static string ProfileKey(string id) => "profile:" + id;

    private static string FolderKey(string folder) => "folder:" + folder.ToUpperInvariant();

    // Games by name, then Monitor Recordings, then Other.
    private static int GameRank(string key) => key == OtherKey ? 2 : key == MonitorRecordingsKey ? 1 : 0;

    private void SyncGames(Dictionary<string, string> games)
    {
        var wanted = new List<LibraryGameOption>(games.Count + 1) { _allGames };
        foreach (var (key, name) in games
            .OrderBy(g => GameRank(g.Key))
            .ThenBy(g => g.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!_gameOptions.TryGetValue(key, out var option))
            {
                option = new LibraryGameOption(key);
                _gameOptions.Add(key, option);
            }

            option.Name = name;
            wanted.Add(option);
        }

        foreach (var gone in _gameOptions.Keys.Where(k => !games.ContainsKey(k)).ToList())
        {
            _gameOptions.Remove(gone);
        }

        Sync(Games, wanted);

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

        var zone = _time.LocalTimeZone;
        var today = _time.GetLocalNow().Date;
        var from = _dateFilter switch
        {
            LibraryDateFilter.Today => today,
            LibraryDateFilter.Last7Days => today.AddDays(-6),
            LibraryDateFilter.Last30Days => today.AddDays(-29),
            _ => DateTime.MinValue,
        };

        var search = SearchText.Trim();
        var filtered = new List<(LibraryRow Row, LibraryItem Item, string Key, DateTime Day)>(_shown.Count);
        foreach (var (row, item, key) in _shown)
        {
            var day = TimeZoneInfo.ConvertTime(item.Date, zone).Date;
            if ((_gameFilter is null || key == _gameFilter)
                && day >= from
                && (_dateFilter == LibraryDateFilter.All || day <= today)
                && (!_favouritesOnly || item.Favourite)
                && (search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add((row, item, key, day));
            }
        }

        // The sort orders rows within each group; GroupBy keeps that order inside each group.
        var comparer = new LibraryRowComparer(_sort, key => _gameOptions.TryGetValue(key, out var o) ? o.Name : key);
        filtered.Sort((a, b) => comparer.Compare((a.Item, a.Key), (b.Item, b.Key)));

        var wanted = new List<object>(filtered.Count + 16);
        if (_groupBy == LibraryGrouping.Game)
        {
            // Games in the combo's order.
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 1; i < Games.Count; i++)
            {
                order[Games[i].Key!] = i;
            }

            foreach (var group in filtered.GroupBy(f => f.Key).OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue)))
            {
                wanted.Add(Header("game|" + group.Key, _gameOptions.TryGetValue(group.Key, out var o) ? o.Name : group.Key));
                wanted.AddRange(group.Select(f => f.Row));
            }
        }
        else if (_groupBy == LibraryGrouping.Day)
        {
            // Days newest first, or oldest first under Oldest first.
            var days = filtered.GroupBy(f => f.Day);
            foreach (var group in _sort == LibrarySort.OldestFirst ? days.OrderBy(g => g.Key) : days.OrderByDescending(g => g.Key))
            {
                wanted.Add(Header("day|" + group.Key.ToString(DayFormat, CultureInfo.InvariantCulture), DayName(group.Key, today)));
                wanted.AddRange(group.Select(f => f.Row));
            }
        }
        else
        {
            wanted.AddRange(filtered.Select(f => f.Row));
        }

        Sync(Entries, wanted);
        _shownRows = wanted.OfType<LibraryRow>().ToList();
        _shownIndex = new Dictionary<string, int>(_shownRows.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _shownRows.Count; i++)
        {
            _shownIndex[_shownRows[i].Path] = i;
        }

        OnPropertyChanged(nameof(NoResultsText));
        RefreshSelection();
    }

    private static string DayName(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString(DayFormat, CultureInfo.InvariantCulture);

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

    private bool SetView<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name))
        {
            return false;
        }

        Apply();
        SaveView();
        return true;
    }

    private void SaveView() =>
        new LibraryViewState(_groupBy, _gameFilter, _dateFilter, _favouritesOnly, _sort, _playerVolume, _playerMuted).Save(_viewStateFile);

    // The player's volume and mute, held as they change and written when the player page closes.
    private void OnPlayerVolumeChanged(double volume, bool muted) => (_playerVolume, _playerMuted) = (volume, muted);

    /// <summary>Makes the target list equal the wanted one in place, keeping shared items; a reorder resets the list.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
        where T : class
    {
        var position = new Dictionary<T, int>(wanted.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < wanted.Count; i++)
        {
            position[wanted[i]] = i;
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!position.ContainsKey(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 1; i < target.Count; i++)
        {
            if (position[target[i]] < position[target[i - 1]])
            {
                target.Clear();
                break;
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= target.Count || !ReferenceEquals(target[i], wanted[i]))
            {
                target.Insert(i, wanted[i]);
            }
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

    private RelayCommand Selection(Action execute, Func<bool> canExecute)
    {
        var command = new RelayCommand(execute, () => !_deleting && canExecute());
        _selectionCommands.Add(command);
        return command;
    }

    // Sets each row's flags from the set, then tells the command bar and the commands.
    private void RefreshSelection()
    {
        _shownSelected = _shownRows.Where(r => _selected.Contains(r.Path)).ToList();
        foreach (var row in _rows.Values)
        {
            row.IsSelected = _selected.Contains(row.Path);
            row.IsFocused = Same(row.Path, _focus);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasShownSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HiddenText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(FavouriteLabel));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(DeleteToolTip));
        OnPropertyChanged(nameof(FocusedRow));
        foreach (var command in _selectionCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>A click on a row, by Explorer's rules: plain selects it, Ctrl toggles it, Shift selects from the anchor.</summary>
    public void Click(LibraryRow row, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_shownIndex.ContainsKey(row.Path))
        {
            return;
        }

        if (shift)
        {
            SelectRange(row.Path, add: ctrl);
        }
        else if (ctrl)
        {
            if (!_selected.Remove(row.Path))
            {
                _selected.Add(row.Path);
            }

            _anchor = row.Path;
        }
        else
        {
            _selected.Clear();
            _selected.Add(row.Path);
            _anchor = row.Path;
        }

        _focus = row.Path;
        RefreshSelection();
    }

    /// <summary>The arrow and paging keys: plain selects the target, Shift selects from the anchor, Ctrl moves only the focus.</summary>
    public void MoveFocus(LibraryMove move, int pageRows, bool ctrl, bool shift)
    {
        if (_shownRows.Count == 0)
        {
            return;
        }

        var current = FocusedRow is { } focused ? _shownIndex[focused.Path] : -1;
        var page = Math.Max(1, pageRows);
        var target = move switch
        {
            LibraryMove.Up => current < 0 ? 0 : current - 1,
            LibraryMove.Down => current < 0 ? 0 : current + 1,
            LibraryMove.PageUp => current < 0 ? 0 : current - page,
            LibraryMove.PageDown => current < 0 ? 0 : current + page,
            LibraryMove.Home => 0,
            _ => _shownRows.Count - 1,
        };
        var path = _shownRows[Math.Clamp(target, 0, _shownRows.Count - 1)].Path;

        if (shift)
        {
            SelectRange(path, add: ctrl);
        }
        else if (!ctrl)
        {
            _selected.Clear();
            _selected.Add(path);
            _anchor = path;
        }

        _focus = path;
        RefreshSelection();
    }

    // Selects (or adds) the shown rows from the anchor to path; a missing anchor becomes path.
    private void SelectRange(string path, bool add)
    {
        if (_anchor is null || !_shownIndex.ContainsKey(_anchor))
        {
            _anchor = path;
        }

        var (from, to) = (_shownIndex[_anchor], _shownIndex[path]);
        if (!add)
        {
            _selected.Clear();
        }

        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _selected.Add(_shownRows[i].Path);
        }
    }

    /// <summary>Ctrl+A: the selection becomes every shown row.</summary>
    public void SelectAll()
    {
        _selected.Clear();
        _selected.UnionWith(_shownRows.Select(r => r.Path));
        RefreshSelection();
    }

    /// <summary>Ctrl+Space: toggles the focused row, which becomes the anchor.</summary>
    public void ToggleFocusedSelection()
    {
        if (FocusedRow is { } row)
        {
            Click(row, ctrl: true, shift: false);
        }
    }

    /// <summary>Esc, the ✕, or a click on empty space: nothing selected; the focus stays.</summary>
    public void ClearSelection()
    {
        _selected.Clear();
        RefreshSelection();
    }

    /// <summary>Before the context menu opens: a row outside the selection becomes the only one selected.</summary>
    public void PrepareContextMenu(LibraryRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_selected.Contains(row.Path))
        {
            Click(row, ctrl: false, shift: false);
        }
        else if (!Same(_focus, row.Path) && _shownIndex.ContainsKey(row.Path))
        {
            _focus = row.Path;
            RefreshSelection();
        }
    }

    /// <summary>Enter or a double-click: plays the focused row in the app; a row still being written does nothing.</summary>
    public void OpenFocused()
    {
        if (!_deleting && FocusedRow is { IsLocked: false } row)
        {
            Play(row);
        }
    }

    /// <summary>A click on a row: selects it by the Ctrl and Shift rules, and a plain double click also opens it.</summary>
    public void ClickRow(LibraryRow row, int clickCount, bool ctrl, bool shift)
    {
        Click(row, ctrl, shift);
        if (clickCount == 2 && !ctrl && !shift)
        {
            OpenFocused();
        }
    }

    /// <summary>Readies the selection for the context menu. False when the menu has nothing to act on, so it stays shut.</summary>
    public bool TryPrepareContextMenu(bool fromKeyboard, bool onRow)
    {
        if (fromKeyboard)
        {
            PrepareContextMenu(FocusedRow);
        }
        else if (!onRow)
        {
            return false;
        }

        return HasShownSelection;
    }

    /// <summary>The row that takes keyboard focus as it enters the list: the focused row, else the first.</summary>
    public LibraryRow? FocusForEntry()
    {
        if (FocusedRow is null)
        {
            MoveFocus(LibraryMove.Home, 0, ctrl: true, shift: false);
        }

        return FocusedRow;
    }

    /// <summary>Runs the list's shortcut for a key that doesn't move the focus. False when the key isn't one.</summary>
    public bool HandleKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.A when ctrl:
                SelectAll();
                break;
            case Key.Space when ctrl:
                ToggleFocusedSelection();
                break;
            case Key.Space:
                ToggleFocusedFavourite();
                break;
            case Key.Enter:
                OpenFocused();
                break;
            case Key.Delete:
                DeleteSelection();
                break;
            case Key.F2:
                RenameCommand.Execute(null);
                break;
            case Key.C when ctrl:
                CopyCommand.Execute(null);
                break;
            case Key.Escape:
                ClearSelection();
                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>Pauses the player, if one is playing: the window was hidden.</summary>
    public void PausePlayer()
    {
        Player?.Pause();
        Creator?.PauseAndCancel();
    }

    public void ResumeCreator() => Creator?.Resume();

    /// <summary>Space: toggles the focused row's Favourite.</summary>
    public void ToggleFocusedFavourite()
    {
        if (FocusedRow is { } row)
        {
            _ = SetFavouriteAsync(row);
        }
    }

    private static bool IsDeletable(LibraryRow row) => !row.IsFavourite && !row.IsLocked && row.Item is not null;

    private static string NotDeletableText(IReadOnlyList<LibraryRow> rows)
    {
        var favourites = rows.Count(r => r.IsFavourite);
        if (favourites == rows.Count)
        {
            return rows.Count == 1
                ? "Favourites can't be deleted. Remove it from Favourites first."
                : "Favourites can't be deleted. Remove them from Favourites first.";
        }

        if (favourites == 0)
        {
            return rows.Count == 1 ? "That file is still being written." : "Those files are still being written.";
        }

        return "Favourites and files being saved can't be deleted.";
    }

    private string? Absolute(LibraryRow row) =>
        string.IsNullOrEmpty(_libraryFolder) ? null : System.IO.Path.Combine(_libraryFolder, row.Path);

    // The rows' absolute paths whose files still exist; only existence is checked, never the contents.
    private List<string>? Existing(IReadOnlyList<LibraryRow> rows)
    {
        var paths = rows.Select(Absolute).OfType<string>().Where(System.IO.File.Exists).ToList();
        if (paths.Count == 0)
        {
            Fail(rows[0].Path, "That file no longer exists.");
            return null;
        }

        return paths;
    }

    // One shell call: a success clears the error, any failure shows the short message.
    private void Shell(string row, Action action, string failure)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Any shell failure is shown as a short error, never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            Fail(row, failure);
            return;
        }

        ClearActionError();
    }

    private void Open(LibraryRow row)
    {
        if (Existing([row]) is [var path])
        {
            Shell(row.Path, () => _shell.Open(path), "Couldn't open the file.");
        }
    }

    private void OpenSelection()
    {
        if (_shownSelected is [var row])
        {
            Open(row);
        }
    }

    private void PlaySelection()
    {
        if (_shownSelected is [{ IsLocked: false } row])
        {
            Play(row);
        }
    }

    // Opens the row's player page in the drill-in, then the file; the page is disposed when the drill-in closes.
    private void Play(LibraryRow row)
    {
        if (Existing([row]) is not [var path])
        {
            return;
        }

        DrillIn.Close();
        ClearActionError();
        var page = new PlayerPage(
            path,
            row.Name,
            _newPlayer(),
            _openRead,
            p => Shell(row.Path, () => _shell.Open(p), "Couldn't open the file."),
            link => Shell(row.Path, () => _shell.Open(link), "Couldn't open the Microsoft Store."),
            _log)
        {
            Volume = _playerVolume,
            IsMuted = _playerMuted,
        };
        page.VolumeChanged += OnPlayerVolumeChanged;
        page.CreateClip = () => CreateClip(row, page.Position.TotalSeconds);
        Player = page;
        DrillIn.Open(row.Path, row.Name);
        _ = page.OpenAsync();
    }

    private void ClosePlayer()
    {
        var page = Player;
        Player = null;
        if (page is null)
        {
            return;
        }

        page.VolumeChanged -= OnPlayerVolumeChanged;
        page.Dispose();
        SaveView();
    }

    private void CreateClipSelection()
    {
        if (_shownSelected is [{ IsLocked: false } row]) { CreateClip(row, 0); }
    }

    private void CreateClip(LibraryRow row, double position)
    {
        if (row.IsLocked || row.Item is not { BeingWritten: false } item || Existing([row]) is not [var path]) { return; }
        try
        {
            var file = new FileInfo(path);
            if (file.Length != item.Size || item.Modified is { } modified && file.LastWriteTimeUtc != modified.UtcDateTime)
            { throw new IOException("This source changed. Wait for the Library to refresh, then reopen it."); }
            var source = new ClipSource(path, file.Length, file.LastWriteTimeUtc.Ticks);
            DrillIn.Close();
            _creatorItem = item;
            Creator = new ClipCreatorPage(source, row.Name, _clipMedia, _newPlayer, thumbnailCacheFolder: _clipThumbnailCacheFolder) { BackCommand = DrillIn.BackCommand };
            Creator.SourceInvalidated = DrillIn.Close;
            DrillIn.Open(row.Path, "Create clip · " + row.Name);
            _ = Creator.OpenAsync(position);
        }
        catch (IOException error) { ActionError = error.Message; }
        catch (UnauthorizedAccessException error) { ActionError = error.Message; }
    }

    private void CloseCreator()
    {
        var creator = Creator;
        Creator = null;
        _creatorItem = null;
        creator?.Dispose();
    }

    private void CopySelection()
    {
        var rows = _shownSelected.Where(r => !r.IsLocked).ToList();
        if (rows.Count > 0 && Existing(rows) is { } paths)
        {
            Shell(rows[0].Path, () => _shell.Copy(paths), paths.Count == 1 ? "Couldn't copy the file." : "Couldn't copy the files.");
        }
    }

    private void CopySelectionToFolder()
    {
        var rows = _shownSelected.Where(r => !r.IsLocked).ToList();
        if (rows.Count > 0 && Existing(rows) is { } paths && _pickFolder() is { } folder)
        {
            Shell(rows[0].Path, () => _shell.CopyToFolder(paths, folder), "Couldn't copy the files.");
        }
    }

    // One Explorer window per folder with its files selected; past three folders, the Library's folder.
    private void ShowSelectionInExplorer()
    {
        var rows = _shownSelected.ToList();
        if (rows.Count == 0 || Existing(rows) is not { } paths)
        {
            return;
        }

        var folders = paths
            .GroupBy(p => System.IO.Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key, Files: (IReadOnlyList<string>)g.ToList()))
            .ToList();
        Shell(
            rows[0].Path,
            () =>
            {
                if (folders.Count > MaxExplorerWindows)
                {
                    _shell.OpenFolder(_libraryFolder!);
                    return;
                }

                foreach (var (folder, files) in folders)
                {
                    _shell.ShowInExplorer(folder, files);
                }
            },
            paths.Count == 1 ? "Couldn't show the file." : "Couldn't show the files.");
    }

    private void RenameSelection()
    {
        if (_shownSelected is [var row])
        {
            row.RenameCommand.Execute(null);
        }
    }

    private void Fail(string? path, string message)
    {
        _actionErrorPath = path;
        ActionError = message;
    }

    private void ClearActionError()
    {
        _actionErrorPath = null;
        ActionError = null;
    }

    // The row's identity as last shown; the star follows the snapshot, never this send.
    private async Task SetFavouriteAsync(LibraryRow row)
    {
        if (row.Item is not { } item)
        {
            return;
        }

        await SendFavouriteAsync(item, !item.Favourite);
    }

    private async Task SendFavouriteAsync(LibraryItem item, bool favourite)
    {
        try
        {
            await _engine.SendAsync(LibraryMessages.SetFavourite, new LibrarySetFavourite(item.Path, item.Size, item.Modified, favourite));
        }
        catch (ErrorCodeException)
        {
            // A stale identity or a lost engine; the snapshot carries the truth.
        }
    }

    // Every shown selected item to the bar's state, one send per item that changes, in shown order.
    private async Task FavouriteSelectionAsync()
    {
        var rows = _shownSelected.ToList();
        var favourite = !rows.All(r => r.IsFavourite);
        foreach (var item in rows.Where(r => r.IsFavourite != favourite).Select(r => r.Item).OfType<LibraryItem>().ToList())
        {
            await SendFavouriteAsync(item, favourite);
        }
    }

    /// <summary>The base name a rename sends: trimmed, without one trailing <c>.mp4</c>.</summary>
    private static string BaseName(string text)
    {
        var name = text.Trim();
        return name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    // The engine renames by the identity last shown; the row and the selection follow the snapshot to the new path.
    private async Task RenameAsync(LibraryRow row, string text)
    {
        if (row.Item is not { } item)
        {
            return;
        }

        var name = BaseName(text);
        _renames[item.Path] = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path) ?? "", name + ".mp4");
        try
        {
            await _engine.SendAsync(LibraryMessages.Rename, new LibraryRename(item.Path, item.Size, item.Modified, name));
            ClearActionError();
        }
        catch (ErrorCodeException e)
        {
            _renames.Remove(item.Path);
            Fail(row.Path, RefusalText(e.Code, "Couldn't rename the file."));
        }
    }

    /// <summary>Delete: one confirmation for the selected items that aren't Favourites or being written, then one delete each.</summary>
    public void DeleteSelection()
    {
        if (_deleting || _shownSelected.Count == 0)
        {
            return;
        }

        var shown = _shownSelected.ToList();
        var deletable = shown.Where(IsDeletable).ToList();
        if (deletable.Count == 0)
        {
            Fail(shown[0].Path, NotDeletableText(shown));
            return;
        }

        if (_confirmDelete(DeleteQuestion(shown, deletable)))
        {
            _ = DeleteAsync(deletable.ConvertAll(r => r.Item!));
        }
    }

    private static string DeleteQuestion(List<LibraryRow> shown, List<LibraryRow> deletable)
    {
        var question = deletable.Count == 1
            ? $"Move “{deletable[0].Name}” to the Recycle Bin?"
            : string.Create(CultureInfo.InvariantCulture, $"Move {deletable.Count} items ({SizeText(deletable.Sum(r => r.Item!.Size))}) to the Recycle Bin?");

        var favourites = shown.Count(r => r.IsFavourite);
        var saving = shown.Count(r => !r.IsFavourite && r.IsLocked);
        var kept = new List<string>(2);
        if (favourites > 0)
        {
            kept.Add(favourites == 1 ? "1 Favourite" : string.Create(CultureInfo.InvariantCulture, $"{favourites} Favourites"));
        }

        if (saving > 0)
        {
            kept.Add(saving == 1 ? "1 file being saved" : string.Create(CultureInfo.InvariantCulture, $"{saving} files being saved"));
        }

        return kept.Count == 0
            ? question
            : $"{question}\n{string.Join(" and ", kept)} {(favourites + saving == 1 ? "is" : "are")} kept.";
    }

    // One send at a time in shown order, by the identity as shown; the rows leave as the snapshot says.
    private async Task DeleteAsync(List<LibraryItem> items)
    {
        _deleting = true;
        _deleteTotal = items.Count;
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        string? firstFailed = null;
        var deleted = 0;
        var starred = 0;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                _deleteDone = i + 1;
                RefreshSelection();
                var item = items[i];

                // A row starred while the batch runs is a Favourite now, and a Favourite is never deleted.
                if (_rows.TryGetValue(item.Path, out var row) && row.IsFavourite)
                {
                    starred++;
                    continue;
                }

                try
                {
                    await _engine.SendAsync(LibraryMessages.Delete, new LibraryDelete(item.Path, item.Size, item.Modified));
                    deleted++;
                }
                catch (ErrorCodeException e)
                {
                    failures[e.Code] = failures.GetValueOrDefault(e.Code) + 1;
                    firstFailed ??= item.Path;
                }
            }
        }
        finally
        {
            _deleting = false;
            RefreshSelection();
        }

        if (failures.Count == 0)
        {
            ClearActionError();
            return;
        }

        // The most common reason wins; a tie goes to the first seen.
        var code = failures.MaxBy(f => f.Value).Key;
        var sent = items.Count - starred;
        var failed = sent - deleted;
        Fail(firstFailed, sent == 1
            ? RefusalText(code, "Couldn't delete the file.")
            : string.Create(CultureInfo.InvariantCulture, $"Deleted {deleted} of {sent}. ")
                + RefusalText(code, failed == 1 ? "1 file couldn't be deleted." : string.Create(CultureInfo.InvariantCulture, $"{failed} files couldn't be deleted.")));
    }

    private static string RefusalText(string code, string otherwise) => code switch
    {
        ErrorCodes.FileBeingWritten => "That file is still being written.",
        ErrorCodes.InvalidFileName => "That name can't be used for a file.",
        ErrorCodes.FileNameTaken => "A file with that name already exists.",
        ErrorCodes.RecycleBinUnavailable => "That drive has no Recycle Bin, so the file wasn't deleted.",
        ErrorCodes.LibraryItemChanged => "That file changed; try again.",
        _ => otherwise,
    };

    private bool ConfirmDelete(string question) =>
        Prompts.YesNo(_view, question, "Delete", MessageBoxImage.Question);

    private string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Copy to folder" };
        var owner = _view is null ? null : Window.GetWindow(_view);
        var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return picked == true ? dialog.FolderName : null;
    }

    private async Task AcknowledgeAsync()
    {
        if (_corruptWarningId is not { } id)
        {
            return;
        }

        try
        {
            await _engine.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(id));
        }
        catch (ErrorCodeException)
        {
            // Already dismissed; the next snapshot clears the banner.
        }
    }

    /// <summary>A size in MB below 1 GB, else in GB (1024-based), with one decimal.</summary>
    internal static string SizeText(long bytes)
    {
        const double Mb = 1024 * 1024;
        const double Gb = Mb * 1024;
        // Compared after rounding, so a size just under 1 GB doesn't read "1024.0 MB".
        return Math.Round(bytes / Mb, 1) < 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / Mb:0.0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / Gb:0.0} GB");
    }

    /// <summary>A length as <c>m:ss</c>, or <c>h:mm:ss</c> from an hour; empty when unknown.</summary>
    internal static string LengthText(double? seconds)
    {
        if (seconds is not { } value || double.IsNaN(value) || value < 0)
        {
            return "";
        }

        var whole = (long)Math.Round(value);
        var (hours, minutes, rest) = (whole / 3600, whole / 60 % 60, whole % 60);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{rest:00}");
    }
}

/// <summary>One Library item as the page shows it; setFavourite and rename send the row's commands.</summary>
internal sealed class LibraryRow(
    string path,
    Action<LibraryRow>? setFavourite = null,
    Action<LibraryRow, string>? rename = null) : Observable
{
    private string _game = "";
    private string _date = "";
    private string _size = "";
    private string _length = "";
    private ImageSource? _thumbnail;
    private bool _isLocked;
    private bool _isFavourite;
    private bool _isSelected;
    private bool _isFocused;
    private RelayCommand? _favouriteCommand;
    private RelayCommand? _renameCommand;
    private RelayCommand? _commitRenameCommand;
    private RelayCommand? _cancelRenameCommand;
    private bool _isRenaming;
    private string _renameText = "";
    private string _badge = "";

    /// <summary>The item's key: its path relative to the Output Folder.</summary>
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    /// <summary>True: a row, which the list can select and focus, unlike a group header.</summary>
    public bool IsRow => true;

    /// <summary>Whether the page's selection holds this item; set only by the page.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>Whether this is the page's focused row; set only by the page.</summary>
    public bool IsFocused
    {
        get => _isFocused;
        internal set => Set(ref _isFocused, value);
    }

    public string Game
    {
        get => _game;
        private set => Set(ref _game, value);
    }

    /// <summary>The local date, <c>yyyy-MM-dd HH:mm</c>.</summary>
    public string Date
    {
        get => _date;
        private set => Set(ref _date, value);
    }

    public string Size
    {
        get => _size;
        private set => Set(ref _size, value);
    }

    /// <summary>The length, <c>m:ss</c> or <c>h:mm:ss</c>; empty while unknown or being written.</summary>
    public string Length
    {
        get => _length;
        private set => Set(ref _length, value);
    }

    /// <summary>The shell's thumbnail, or null for the generic glyph.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (Set(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>The absolute path and size the thumbnail was last asked for; null while none is.</summary>
    internal (string Path, long Size)? ThumbnailRequest { get; set; }

    /// <summary>Whether the file is still being written; the row shows "Saving…" instead of a size.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (Set(ref _isLocked, value))
            {
                OnPropertyChanged(nameof(Lock));
                _renameCommand?.RaiseCanExecuteChanged();
                if (value)
                {
                    IsRenaming = false;
                }
            }
        }
    }

    /// <summary>The lock marker, empty once written.</summary>
    public string Lock => IsLocked ? "Saving…" : "";

    /// <summary>Whether the item is a Favourite, as the last snapshot said.</summary>
    public bool IsFavourite
    {
        get => _isFavourite;
        private set
        {
            if (Set(ref _isFavourite, value))
            {
                OnPropertyChanged(nameof(Star));
            }
        }
    }

    /// <summary>The star: filled for a Favourite.</summary>
    public string Star => IsFavourite ? "★" : "☆";

    /// <summary>Toggles the Favourite through the engine.</summary>
    public ICommand FavouriteCommand => _favouriteCommand ??= new RelayCommand(() => setFavourite?.Invoke(this));

    /// <summary>Whether the name is being edited inline.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        private set => Set(ref _isRenaming, value);
    }

    /// <summary>The inline editor's text.</summary>
    public string RenameText
    {
        get => _renameText;
        set => Set(ref _renameText, value);
    }

    /// <summary>Starts editing the name inline, from its base name; disabled until the file is written.</summary>
    public ICommand RenameCommand => _renameCommand ??= new RelayCommand(
        () =>
        {
            RenameText = System.IO.Path.GetFileNameWithoutExtension(Name);
            IsRenaming = true;
        },
        () => !IsLocked);

    /// <summary>Ends the edit and sends the rename; a no-op once the edit has ended.</summary>
    public ICommand CommitRenameCommand => _commitRenameCommand ??= new RelayCommand(() =>
    {
        if (IsRenaming)
        {
            IsRenaming = false;
            rename?.Invoke(this, RenameText);
        }
    });

    /// <summary>Ends the edit, sending nothing.</summary>
    public ICommand CancelRenameCommand => _cancelRenameCommand ??= new RelayCommand(() => IsRenaming = false);

    /// <summary>The kind badge on the thumbnail: "Recording" or "Monitor Recording"; empty for a Clip or an unknown file.</summary>
    public string Badge
    {
        get => _badge;
        private set
        {
            if (Set(ref _badge, value))
            {
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool HasBadge => _badge.Length > 0;

    /// <summary>The item last shown, whose identity (path, size, modified time) a command sends.</summary>
    internal LibraryItem? Item { get; private set; }

    public void Show(LibraryItem item, string game)
    {
        Item = item;
        IsFavourite = item.Favourite;
        Game = game;
        Date = item.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Size = item.BeingWritten ? "" : LibraryPage.SizeText(item.Size);
        Length = item.BeingWritten ? "" : LibraryPage.LengthText(item.LengthSeconds);
        IsLocked = item.BeingWritten;
        Badge = item.Kind switch
        {
            LibraryItemKinds.Recording => "Recording",
            LibraryItemKinds.MonitorRecording => "Monitor Recording",
            _ => "",
        };
    }
}

/// <summary>A key move in the Library list.</summary>
internal enum LibraryMove
{
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
}

/// <summary>A group's header in the Library list; one instance per group key, its name kept current.</summary>
internal sealed class LibraryGroupHeader(string key) : Observable
{
    private string _name = "";

    public string Key { get; } = key;

    /// <summary>False: a header, which the list never selects or focuses.</summary>
    public bool IsRow => false;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
}

/// <summary>One entry of the game filter, one per game key, so a renamed profile keeps its selection; null is "All games".</summary>
internal sealed class LibraryGameOption(string? key) : Observable
{
    private string _name = "";

    public string? Key { get; } = key;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
}

/// <summary>The Library's row order for a sort; ties go by date (newest first), then path.</summary>
internal sealed class LibraryRowComparer(LibrarySort sort, Func<string, string> gameName) : IComparer<(LibraryItem Item, string Key)>
{
    public int Compare((LibraryItem Item, string Key) x, (LibraryItem Item, string Key) y)
    {
        var (a, b) = (x.Item, y.Item);
        var order = sort switch
        {
            LibrarySort.OldestFirst => a.Date.CompareTo(b.Date),
            LibrarySort.Name => NaturalCompare(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path)),
            LibrarySort.LargestFirst => b.Size.CompareTo(a.Size),
            LibrarySort.LongestFirst => (a.LengthSeconds, b.LengthSeconds) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (la, lb) => lb!.Value.CompareTo(la!.Value),
            },
            LibrarySort.Game => string.Compare(gameName(x.Key), gameName(y.Key), StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };

        if (order == 0 && sort != LibrarySort.OldestFirst)
        {
            order = b.Date.CompareTo(a.Date);
        }

        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
    }

    /// <summary>Compares ignoring case, with each run of digits compared as a number ("Clip 2" before "Clip 10").</summary>
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (si, sj) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var (da, db) = (a[si..i].TrimStart('0'), b[sj..j].TrimStart('0'));
                var number = da.Length != db.Length ? da.Length.CompareTo(db.Length) : string.CompareOrdinal(da, db);
                if (number != 0)
                {
                    return number;
                }

                continue;
            }

            var letter = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (letter != 0)
            {
                return letter;
            }

            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>An option of a view combo: its value and display name.</summary>
internal sealed record LibraryChoice<T>(T Value, string Name);
