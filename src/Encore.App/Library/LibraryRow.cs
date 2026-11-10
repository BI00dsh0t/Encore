using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

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
        Size = item.BeingWritten ? "" : LibraryText.SizeText(item.Size);
        Length = item.BeingWritten ? "" : LibraryText.LengthText(item.LengthSeconds);
        IsLocked = item.BeingWritten;
        Badge = item.Kind switch
        {
            LibraryItemKinds.Recording => "Recording",
            LibraryItemKinds.MonitorRecording => "Monitor Recording",
            _ => "",
        };
    }
}
