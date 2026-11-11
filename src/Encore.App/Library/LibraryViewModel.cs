using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Encore.App.Engine;
using Encore.App.Settings;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The Library page's state: it builds the list, selection, actions and item pages, and gives them each snapshot.</summary>
internal sealed class LibraryViewModel : Observable
{
    private readonly string? _viewStateFile;
    private readonly List<RelayCommand> _selectionCommands = [];
    private FrameworkElement? _view;
    private int _itemCount;
    private bool _hasRendered;

    /// <summary>Each optional argument replaces a real dependency (thumbnails, shell, prompts, player) for tests.</summary>
    public LibraryViewModel(
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
        string? clipThumbnailCacheFolder = null,
        string? firstUseFile = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _viewStateFile = viewStateFile;
        Tips = new LibraryTips(firstUseFile);
        var view = LibraryViewState.Load(viewStateFile);
        List = new LibraryList(view, time ?? TimeProvider.System, thumbnails, NewRow);
        Selection = new LibrarySelection(List);
        Actions = new LibraryFileActions(
            engine,
            shell ?? new WindowsLibraryShell(),
            List,
            Selection,
            confirmDelete ?? (question => LibraryPrompts.ConfirmDelete(_view, question)),
            pickFolder ?? (() => LibraryPrompts.PickFolder(_view)));
        DrillIn = new DrillIn(MainViewModel.LibraryTitle);
        Playback = new LibraryPlayback(
            DrillIn,
            Actions,
            view,
            player ?? (() => new WindowsMediaPlayer()),
            openRead,
            log ?? (_ => { }),
            clipMedia ?? new ClipMediaService(),
            clipThumbnailCacheFolder);
        Banner = new LibraryBanner(engine);

        OpenCommand = SelectionCommand(Actions.OpenSelection, () => Selected.Count == 1);
        PlayCommand = SelectionCommand(PlaySelection, () => Selected is [{ IsLocked: false }]);
        CreateClipCommand = SelectionCommand(CreateClipSelection, () => Selected is [{ IsLocked: false }]);
        FavouriteSelectionCommand = SelectionCommand(() => _ = Actions.FavouriteSelectionAsync(), () => Selected.Count > 0);
        CopyCommand = SelectionCommand(Actions.CopySelection, () => Selected.Any(row => !row.IsLocked));
        CopyToFolderCommand = SelectionCommand(Actions.CopySelectionToFolder, () => Selected.Any(row => !row.IsLocked));
        ShowInExplorerCommand = SelectionCommand(Actions.ShowSelectionInExplorer, () => Selected.Count > 0);
        RenameCommand = SelectionCommand(Actions.RenameSelection, () => Selected is [{ IsLocked: false }]);
        DeleteCommand = SelectionCommand(Actions.DeleteSelection, () => Selected.Any(LibraryDeleteText.IsDeletable));
        ClearSelectionCommand = SelectionCommand(Selection.Clear, () => Selection.HasSelection);

        List.PropertyChanged += OnListPropertyChanged;
        List.ViewChanged += SaveView;
        Selection.Changed += OnSelectionChanged;
        Playback.PlayerClosed += SaveView;
    }

    /// <summary>The rows with their grouping, filters, sort and search.</summary>
    public LibraryList List { get; }

    /// <summary>The selected rows and the focused row.</summary>
    public LibrarySelection Selection { get; }

    /// <summary>What the commands do with the selected rows, and the error line.</summary>
    public LibraryFileActions Actions { get; }

    /// <summary>The player page and the clip creator.</summary>
    public LibraryPlayback Playback { get; }

    /// <summary>The rebuilt-index banner.</summary>
    public LibraryBanner Banner { get; }

    /// <summary>The first-use tips at the top of the Library.</summary>
    public LibraryTips Tips { get; }

    /// <summary>The item pages' drill-in: the breadcrumb, Back and close of the player and the clip creator.</summary>
    public DrillIn DrillIn { get; }

    /// <summary>Whether the empty line shows: a snapshot has come, and the Library holds nothing, with no filter or search.</summary>
    public bool ShowsEmptyMessage => _hasRendered && _itemCount == 0 && !List.IsFiltered;

    /// <summary>Whether the command bar shows: a snapshot has come, and the Library isn't simply empty.</summary>
    public bool ShowsCommandBar => _hasRendered && !ShowsEmptyMessage;

    /// <summary>"Open in default player": hands the one selected item to the shell's default player, even while being written.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>"Play": plays the one shown selected item on the in-app player page, once it's written.</summary>
    public ICommand PlayCommand { get; }

    /// <summary>Opens the clip creator on the one shown selected item, once it's written.</summary>
    public ICommand CreateClipCommand { get; }

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

    public FrameworkElement View => _view ??= new LibraryView { DataContext = this };

    private IReadOnlyList<LibraryRow> Selected => Selection.ShownSelected;

    /// <summary>Shows <paramref name="state"/>'s Library, changing the rows in place.</summary>
    public void Render(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Tips.Show(state);
        if (!_hasRendered || _itemCount != state.Library.Count)
        {
            _hasRendered = true;
            _itemCount = state.Library.Count;
            OnEmptyChanged();
        }

        Banner.Show(state);
        Actions.ShowFolder(state.LibraryFolder);

        // The parts follow the new rows before the list on screen changes, so each change is drawn once.
        List.Read(state);
        Actions.ForgetErrorOfMissingRow();
        Selection.FollowSnapshot();
        Playback.CloseIfSourceLeft(List, state.LibraryFolder);
        List.Publish();
        Selection.RestoreFocus();
    }

    /// <summary>A click on a row: selects it by the Ctrl and Shift rules, and a plain double click also opens it.</summary>
    public void ClickRow(LibraryRow row, int clickCount, bool ctrl, bool shift)
    {
        Selection.Click(row, ctrl, shift);
        if (clickCount == 2 && !ctrl && !shift)
        {
            OpenFocused();
        }
    }

    /// <summary>Enter or a double-click: plays the focused row in the app; a row still being written does nothing.</summary>
    public void OpenFocused()
    {
        if (!Actions.IsDeleting && Selection.FocusedRow is { IsLocked: false } row)
        {
            Playback.Play(row);
        }
    }

    /// <summary>Runs the list's shortcut for a key that doesn't move the focus. False when the key isn't one.</summary>
    public bool HandleKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.A when ctrl:
                Selection.SelectAll();
                break;
            case Key.Space when ctrl:
                Selection.ToggleFocused();
                break;
            case Key.Space:
                ToggleFocusedFavourite();
                break;
            case Key.Enter:
                OpenFocused();
                break;
            case Key.Delete:
                Actions.DeleteSelection();
                break;
            case Key.F2:
                RenameCommand.Execute(null);
                break;
            case Key.C when ctrl:
                CopyCommand.Execute(null);
                break;
            case Key.Escape:
                Selection.Clear();
                break;
            default:
                return false;
        }

        return true;
    }

    private LibraryRow NewRow(string path) =>
        new(path, row => _ = Actions.ToggleFavouriteAsync(row), (row, name) => _ = Actions.RenameAsync(row, name));

    // A command on the selection: it waits while a delete runs, and is asked again whenever the selection changes.
    private RelayCommand SelectionCommand(Action execute, Func<bool> canExecute)
    {
        var command = new RelayCommand(execute, () => !Actions.IsDeleting && canExecute());
        _selectionCommands.Add(command);
        return command;
    }

    private void OnSelectionChanged()
    {
        foreach (var command in _selectionCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    // The welcome card follows the filters and search.
    private void OnListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryList.GameFilter) or nameof(LibraryList.DateFilter)
            or nameof(LibraryList.FavouritesOnly) or nameof(LibraryList.SearchText))
        {
            OnEmptyChanged();
        }
    }

    private void OnEmptyChanged()
    {
        OnPropertyChanged(nameof(ShowsEmptyMessage));
        OnPropertyChanged(nameof(ShowsCommandBar));
    }

    private void SaveView() => List.ViewState(Playback.Volume, Playback.IsMuted).Save(_viewStateFile);

    private void PlaySelection()
    {
        if (Selected is [{ IsLocked: false } row])
        {
            Playback.Play(row);
        }
    }

    private void CreateClipSelection()
    {
        if (Selected is [{ IsLocked: false } row])
        {
            Playback.CreateClip(row, 0);
        }
    }

    // Space: toggles the focused row's Favourite.
    private void ToggleFocusedFavourite()
    {
        if (Selection.FocusedRow is { } row)
        {
            _ = Actions.ToggleFavouriteAsync(row);
        }
    }
}
