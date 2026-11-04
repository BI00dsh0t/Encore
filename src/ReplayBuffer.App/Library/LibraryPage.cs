using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Library;

/// <summary>
/// The Library tab (story 7.1): the snapshot's Library items, newest first, each with its name, game,
/// date and size, and an item still being written shown locked. It only reads the snapshot (AD-1).
/// While Cleanup is blocked by a rebuilt index (story 7.2) a banner says so, and Acknowledge dismisses
/// the <c>library-corrupt</c> warning. Each row shows its length and the shell's thumbnail (story 7.3),
/// asked by absolute path once the item is written, never while it is. Each row's star (story 7.4)
/// sends <c>library.setFavourite</c> and changes only when the snapshot does. The list is grouped by
/// game or by day and filtered by game, date and Favourite (story 7.5), a choice remembered per
/// instance. Members run on the UI thread.
/// </summary>
internal sealed class LibraryPage : Observable
{
    /// <summary>Core's <c>ProfileFolders.MonitorRecordings</c>; the App doesn't reference Core.</summary>
    internal const string MonitorRecordings = "Monitor Recordings";

    private const string OtherKey = "other";
    private static readonly string MonitorRecordingsKey = FolderKey(MonitorRecordings);

    private readonly IEngineSession _engine;
    private readonly LibraryThumbnails? _thumbnails;
    private readonly string? _viewStateFile;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, LibraryRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibraryGroupHeader> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryGameOption> _gameOptions = new(StringComparer.Ordinal);
    private readonly LibraryGameOption _allGames = new(null) { Name = "All games" };
    private List<(LibraryRow Row, LibraryItem Item, string Key)> _shown = [];
    private bool _hasSnapshot;
    private FrameworkElement? _view;
    private bool _isCleanupBlocked;
    private string? _corruptWarningId;
    private LibraryGrouping _groupBy;
    private string? _gameFilter;
    private LibraryDateFilter _dateFilter;
    private bool _favouritesOnly;

    /// <param name="thumbnails">Where the rows' thumbnails come from; without it, every row shows the generic glyph.</param>
    /// <param name="viewStateFile">Where the view choices are remembered; without it, they last only this run.</param>
    /// <param name="time">The clock the date filter and day headers read "today" from.</param>
    public LibraryPage(IEngineSession engine, LibraryThumbnails? thumbnails = null, string? viewStateFile = null, TimeProvider? time = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _thumbnails = thumbnails;
        _viewStateFile = viewStateFile;
        _time = time ?? TimeProvider.System;
        AcknowledgeCommand = new RelayCommand(() => _ = AcknowledgeAsync());
        Games.Add(_allGames);

        var view = LibraryViewState.Load(viewStateFile);
        (_groupBy, _gameFilter, _dateFilter, _favouritesOnly) = (view.GroupBy, view.Game, view.Date, view.FavouritesOnly);
    }

    /// <summary>Whether the banner shows: the index was rebuilt and Cleanup is off until acknowledged.</summary>
    public bool IsCleanupBlocked
    {
        get => _isCleanupBlocked;
        private set => Set(ref _isCleanupBlocked, value);
    }

    /// <summary>Sends <c>warning.dismiss</c> for the active <c>library-corrupt</c> warning.</summary>
    public ICommand AcknowledgeCommand { get; }

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

    public FrameworkElement View => _view ??= new LibraryView { DataContext = this };

    /// <summary>Shows <paramref name="state"/>'s Library, changing the rows in place.</summary>
    public void Render(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IsCleanupBlocked = state.CleanupBlocked;
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
                row = new LibraryRow(item.Path, r => _ = SetFavouriteAsync(r));
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

        Sync(Rows, shown.ConvertAll(s => s.Row));
        _shown = shown;
        _hasSnapshot = true;
        SyncGames(games);
        Apply();
    }

    /// <summary>The item's game: its group key and the name its header shows.</summary>
    /// <remarks>
    /// Its profile's live name; else the profile whose folder is the item's top-level folder (scanned
    /// files carry no profile id); else that folder's name; else "Other" at the Output Folder's root.
    /// </remarks>
    internal static (string Key, string Name) GameKeyOf(LibraryItem item, SettingsDocument settings)
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

        var filtered = new List<(LibraryRow Row, string Key, DateTime Day)>(_shown.Count);
        foreach (var (row, item, key) in _shown)
        {
            var day = TimeZoneInfo.ConvertTime(item.Date, zone).Date;
            if ((_gameFilter is null || key == _gameFilter)
                && day >= from
                && (_dateFilter == LibraryDateFilter.All || day <= today)
                && (!_favouritesOnly || item.Favourite))
            {
                filtered.Add((row, key, day));
            }
        }

        var wanted = new List<object>(filtered.Count + 16);
        if (_groupBy == LibraryGrouping.Game)
        {
            // Games in the combo's order; rows stay newest first within each.
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
        else
        {
            // Rows are newest first, so each day's rows are contiguous and the days come newest first.
            DateTime? current = null;
            foreach (var (row, _, day) in filtered)
            {
                if (current != day)
                {
                    current = day;
                    wanted.Add(Header("day|" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DayName(day, today)));
                }

                wanted.Add(row);
            }
        }

        Sync(Entries, wanted);
    }

    private static string DayName(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
        new LibraryViewState(_groupBy, _gameFilter, _dateFilter, _favouritesOnly).Save(_viewStateFile);
        return true;
    }

    /// <summary>Makes <paramref name="target"/> equal <paramref name="wanted"/> in place, keeping the items both share.</summary>
    /// <remarks>
    /// Linear: the unwanted are dropped, and when what is left is still in the wanted order (a snapshot
    /// or a filter change) the new items are merged in; a reorder (a grouping change) resets the list.
    /// </remarks>
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
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

    // The row's identity as last shown (AD-3); the star follows the snapshot, never this send.
    private async Task SetFavouriteAsync(LibraryRow row)
    {
        if (row.Item is not { } item)
        {
            return;
        }

        try
        {
            await _engine.SendAsync(LibraryMessages.SetFavourite, new LibrarySetFavourite(item.Path, item.Size, item.Modified, !item.Favourite));
        }
        catch (ErrorCodeException)
        {
            // A stale identity or a lost engine; the snapshot carries the truth.
        }
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

/// <summary>One Library item as the page shows it.</summary>
/// <param name="setFavourite">Sends the row's Favourite toggle; the star command's action.</param>
internal sealed class LibraryRow(string path, Action<LibraryRow>? setFavourite = null) : Observable
{
    private string _game = "";
    private string _date = "";
    private string _size = "";
    private string _length = "";
    private ImageSource? _thumbnail;
    private bool _isLocked;
    private bool _isFavourite;
    private RelayCommand? _favouriteCommand;

    /// <summary>The item's key: its path relative to the Output Folder.</summary>
    public string Path { get; } = path;

    /// <summary>The file's name.</summary>
    public string Name { get; } = System.IO.Path.GetFileName(path);

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
    }
}

/// <summary>A group's header in the Library list; one instance per group key, its name kept current.</summary>
internal sealed class LibraryGroupHeader(string key) : Observable
{
    private string _name = "";

    public string Key { get; } = key;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
}

/// <summary>One entry of the game filter; one instance per game key, so a renamed profile keeps its selection.</summary>
/// <param name="key">The game's group key; null for "All games".</param>
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

/// <summary>An option of a view combo: its value and display name.</summary>
internal sealed record LibraryChoice<T>(T Value, string Name);
