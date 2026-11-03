using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Library;

/// <summary>
/// The Library tab (story 7.1): the snapshot's Library items, newest first, each with its name, game,
/// date and size, and an item still being written shown locked. It only reads the snapshot (AD-1).
/// While Cleanup is blocked by a rebuilt index (story 7.2) a banner says so, and Acknowledge dismisses
/// the <c>library-corrupt</c> warning. Members run on the UI thread.
/// </summary>
internal sealed class LibraryPage : Observable
{
    private readonly IEngineSession _engine;
    private FrameworkElement? _view;
    private bool _isCleanupBlocked;
    private string? _corruptWarningId;

    public LibraryPage(IEngineSession engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
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
                row = new LibraryRow(item.Path);
                Rows.Insert(index, row);
            }
            else if (Rows.IndexOf(row) != index)
            {
                Rows.Move(Rows.IndexOf(row), index);
            }

            row.Show(item, GameOf(item, state.Settings));
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

    /// <summary>A size in B, KB, MB or GB (1024-based), with one decimal above bytes.</summary>
    internal static string SizeText(long bytes)
    {
        string[] units = ["KB", "MB", "GB"];
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        var value = (double)bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

/// <summary>One Library item as the page shows it.</summary>
internal sealed class LibraryRow(string path) : Observable
{
    private string _game = "";
    private string _date = "";
    private string _size = "";
    private bool _isLocked;

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

    public void Show(LibraryItem item, string game)
    {
        Game = game;
        Date = item.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Size = item.BeingWritten ? "" : LibraryPage.SizeText(item.Size);
        IsLocked = item.BeingWritten;
    }
}
