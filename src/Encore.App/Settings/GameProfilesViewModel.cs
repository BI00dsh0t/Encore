using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Games: every game in the document, Ignored ones last, each opening its own page. UI thread.</summary>
internal sealed class GameProfilesViewModel : SettingsViewModel
{
    public const string PageTitle = "Games";

    private readonly SettingsSender _sender;
    private readonly IEngineSession _engine;
    private readonly IGameShell _shell;
    private readonly Dictionary<string, GameViewModel> _games = new(StringComparer.Ordinal);
    private StateSnapshot? _state;
    private bool _isOpen;
    private GameViewModel? _openGame;

    public GameProfilesViewModel(SettingsSender sender, IEngineSession engine, IGameShell? shell = null)
        : base(sender)
    {
        _sender = sender;
        _engine = engine;
        _shell = shell ?? WindowsGameShell.Instance;
        DrillIn = new DrillIn(PageTitle);
        DrillIn.Closed += _ => CloseGame();
        AddGame = new FolderPicker(Keys.OutputFolder, _ => Task.FromResult<string?>(null))
        {
            IsFile = true,
            DialogTitle = "Choose the game's program",
            FileFilter = "Programs (*.exe)|*.exe",
            Pick = AddAsync,
        };
    }

    public override string Title => PageTitle;

    /// <inheritdoc/>
    public override DrillIn DrillIn { get; }

    /// <summary>The games: by name, with Ignored games after the others.</summary>
    public ObservableCollection<GameProfileItem> Profiles { get; } = [];

    public bool IsEmpty => Profiles.Count == 0;

    /// <summary>Add a game…: the file variant of the folder picker, filtered to programs.</summary>
    public FolderPicker AddGame { get; }

    /// <summary>The game page on screen, or null on the list.</summary>
    public GameViewModel? OpenGame
    {
        get => _openGame;
        private set
        {
            if (ReferenceEquals(_openGame, value))
            {
                return;
            }

            if (_openGame is { } old)
            {
                old.IsOpen = false;
            }

            _openGame = value;
            if (value is not null)
            {
                value.IsOpen = _isOpen;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>The row whose page was open last: the list focuses it on going back.</summary>
    public string? LastOpenedId { get; private set; }

    /// <summary>Edit the main setting: shows the main page that holds the key. Set by the shell.</summary>
    public Action<SettingKey>? EditMain { get; set; }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges => _games.Values.Any(p => p.HasUnsavedChanges);

    /// <summary>Whether the page is on screen; the open game's audio tracks meter only then.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value))
            {
                return;
            }

            if (_openGame is { } game)
            {
                game.IsOpen = value;
            }
        }
    }

    /// <summary>Opens a game's page in place of the list (click or Enter on its row).</summary>
    public void Open(GameProfileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var viewModel = GameFor(item.ProfileId);
        LastOpenedId = item.ProfileId;
        OpenGame = viewModel;
        DrillIn.Open(item.ProfileId, viewModel.Title);
    }

    /// <summary>The game's page, kept for the run so a draft survives going back.</summary>
    public GameViewModel GameFor(string profileId)
    {
        if (!_games.TryGetValue(profileId, out var viewModel))
        {
            viewModel = new GameViewModel(_sender, _engine, profileId, this, _shell);
            if (_state is { } state)
            {
                viewModel.ShowState(state);
            }

            // A new page shows nothing until rendered, and Render skips a revision already shown.
            viewModel.Render(Document);
            viewModel.PropertyChanged += GameChanged;
            _games.Add(profileId, viewModel);
        }

        return viewModel;
    }

    /// <summary>Takes the snapshot: the game being captured, and each game page's devices and meters. Called before Render.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        foreach (var item in Profiles)
        {
            item.IsCapturing = string.Equals(item.ProfileId, state.TargetGame?.ProfileId, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var viewModel in _games.Values)
        {
            viewModel.ShowState(state);
        }
    }

    protected override FrameworkElement CreateView() => new GameProfilesView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        ShowRows(document);
        OnPropertyChanged(nameof(IsEmpty));
        ForgetRemovedGames(document);
        foreach (var viewModel in _games.Values)
        {
            viewModel.Render(document);
        }

        if (_openGame is { } open)
        {
            DrillIn.ItemTitle = open.Title;
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    // One row per game, in list order: captured games before ignored ones, each by name.
    private void ShowRows(SettingsDocument document)
    {
        var manual = SettingsValues.Global(document, Keys.ManualGames);
        var existing = Profiles.ToDictionary(p => p.ProfileId, StringComparer.Ordinal);
        var ordered = document.Profiles
            .OrderBy(p => p.Value.Ignored)
            .ThenBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var item in Profiles.Where(p => !document.Profiles.ContainsKey(p.ProfileId)).ToList())
        {
            Profiles.Remove(item);
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var (id, profile) = (ordered[index].Key, ordered[index].Value);
            if (!existing.TryGetValue(id, out var item))
            {
                item = new GameProfileItem(id);
                _shell.LoadIcon(id, icon => item.Icon = icon);
                Profiles.Insert(index, item);
            }
            else if (Profiles.IndexOf(item) != index)
            {
                Profiles.Move(Profiles.IndexOf(item), index);
            }

            item.Show(
                profile,
                manual.Any(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase)),
                new[] { Keys.BufferWindow.Name, Keys.Video.Name, Keys.TrackLayout.Name }.Count(profile.Overrides.ContainsKey));
            item.IsCapturing = string.Equals(id, _state?.TargetGame?.ProfileId, StringComparison.OrdinalIgnoreCase);
            item.HasUnsavedChanges = _games.TryGetValue(id, out var cached) && cached.HasUnsavedChanges;
        }
    }

    // A game that went away takes its page with it; an open one returns to the list.
    private void ForgetRemovedGames(SettingsDocument document)
    {
        foreach (var gone in _games.Keys.Where(id => !document.Profiles.ContainsKey(id)).ToList())
        {
            var viewModel = _games[gone];
            viewModel.PropertyChanged -= GameChanged;
            _games.Remove(gone);
            if (ReferenceEquals(viewModel, _openGame))
            {
                DrillIn.Close();
                Message = $"{viewModel.Title} is no longer in the list.";
            }
        }
    }

    private void CloseGame() => OpenGame = null;

    private async Task<string?> AddAsync(string path)
    {
        try
        {
            await _engine.SendAsync(GameMessages.Add, new GameAdd(path));
            Message = $"Added. {Path.GetFileNameWithoutExtension(path)} appears here the next time it runs.";
            return null;
        }
        catch (ErrorCodeException e)
        {
            Message = $"Not added: {e.Message}";
            return e.Code;
        }
    }

    private void GameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GameViewModel viewModel)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(Message):
                Message = viewModel.Message;
                break;
            case nameof(HasUnsavedChanges):
                if (Profiles.FirstOrDefault(p => p.ProfileId == viewModel.ProfileId) is { } row)
                {
                    row.HasUnsavedChanges = viewModel.HasUnsavedChanges;
                }

                OnPropertyChanged(nameof(HasUnsavedChanges));
                break;
            case nameof(Title) when ReferenceEquals(viewModel, _openGame):
                DrillIn.ItemTitle = viewModel.Title;
                break;
        }
    }
}
