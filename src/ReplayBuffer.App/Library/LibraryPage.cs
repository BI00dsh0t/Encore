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
/// sends <c>library.setFavourite</c> and changes only when the snapshot does. Members run on the UI thread.
/// </summary>
internal sealed class LibraryPage : Observable
{
    private readonly IEngineSession _engine;
    private readonly LibraryThumbnails? _thumbnails;
    private FrameworkElement? _view;
    private bool _isCleanupBlocked;
    private string? _corruptWarningId;

    /// <param name="thumbnails">Where the rows' thumbnails come from; without it, every row shows the generic glyph.</param>
    public LibraryPage(IEngineSession engine, LibraryThumbnails? thumbnails = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _thumbnails = thumbnails;
        AcknowledgeCommand = new RelayCommand(() => _ = AcknowledgeAsync());
    }

    /// <summary>Whether the banner shows: the index was rebuilt and Cleanup is off until acknowledged.</summary>
    public bool IsCleanupBlocked
    {
        get => _isCleanupBlocked;
        private set => Set(ref _isCleanupBlocked, value);
    }

    /// <summary>Sends <c>warning.dismiss</c> for the active <c>library-corrupt</c> warning.</summary>
    public ICommand AcknowledgeCommand { get; }

    /// <summary>The items, newest first.</summary>
    public ObservableCollection<LibraryRow> Rows { get; } = [];

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
        var keys = ordered.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = Rows.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var row in Rows.Where(r => !keys.Contains(r.Path)).ToList())
        {
            Rows.Remove(row);
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            if (!existing.TryGetValue(item.Path, out var row))
            {
                row = new LibraryRow(item.Path, r => _ = SetFavouriteAsync(r));
                Rows.Insert(index, row);
            }
            else if (Rows.IndexOf(row) != index)
            {
                Rows.Move(Rows.IndexOf(row), index);
            }

            row.Show(item, GameOf(item, state.Settings));
            RequestThumbnail(row, item, state.LibraryFolder);
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

    /// <summary>The profile's name, else the item's top-level folder, else "Other".</summary>
    internal static string GameOf(LibraryItem item, SettingsDocument settings)
    {
        if (item.ProfileId is { } id && settings.Profiles.TryGetValue(id, out var profile))
        {
            return profile.Name;
        }

        var separator = item.Path.IndexOfAny(['\\', '/']);
        return separator > 0 ? item.Path[..separator] : "Other";
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
