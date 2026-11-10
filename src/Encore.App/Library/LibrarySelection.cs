using System.Globalization;

namespace Encore.App.Library;

/// <summary>Which Library rows are selected and which one has the focus, by Explorer's click and key rules. UI thread.</summary>
internal sealed class LibrarySelection : Observable
{
    private readonly LibraryList _list;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    // The App's own renames in flight: old path → the path the item gets once the snapshot shows it.
    private readonly Dictionary<string, string> _renames = new(StringComparer.OrdinalIgnoreCase);
    private List<LibraryRow> _shownSelected = [];
    private string? _anchor;
    private string? _focus;

    // Set by FollowSnapshot for RestoreFocus: the focused path that left, and the rows shown before it did.
    private string? _lostFocus;
    private IReadOnlyList<LibraryRow> _shownBefore = [];

    public LibrarySelection(LibraryList list)
    {
        _list = list;
        _list.Changed += Refresh;
    }

    /// <summary>Raised after the selection, the focus or the shown rows changed.</summary>
    public event Action? Changed;

    /// <summary>Whether any item is selected, shown or hidden by a filter: the command bar shows its actions.</summary>
    public bool HasSelection => _selected.Count > 0;

    /// <summary>Whether any selected item is shown; the actions act only on those.</summary>
    public bool HasShownSelection => _shownSelected.Count > 0;

    /// <summary>How many items are selected, shown or hidden.</summary>
    public int Count => _selected.Count;

    /// <summary>The selected rows the list shows, in shown order.</summary>
    public IReadOnlyList<LibraryRow> ShownSelected => _shownSelected;

    /// <summary>"({m} hidden by filters)" when a filter hides some selected items; else empty.</summary>
    public string HiddenText => _selected.Count - _shownSelected.Count is var hidden and > 0
        ? string.Create(CultureInfo.InvariantCulture, $"({hidden} hidden by filters)")
        : "";

    /// <summary>Whether exactly one selected item is shown: Open and Rename show.</summary>
    public bool IsSingle => _shownSelected.Count == 1;

    /// <summary>The focused row, when it is shown.</summary>
    public LibraryRow? FocusedRow => _focus is not null && _list.IsShown(_focus) ? _list.ShownRows[_list.ShownIndexOf(_focus)] : null;

    /// <summary>A click on a row, by Explorer's rules: plain selects it, Ctrl toggles it, Shift selects from the anchor.</summary>
    public void Click(LibraryRow row, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_list.IsShown(row.Path))
        {
            return;
        }

        if (shift)
        {
            SelectRange(row.Path, add: ctrl);
        }
        else if (ctrl)
        {
            Toggle(row.Path);
        }
        else
        {
            SelectOnly(row.Path);
        }

        _focus = row.Path;
        Refresh();
    }

    /// <summary>The arrow and paging keys: plain selects the target, Shift selects from the anchor, Ctrl moves only the focus.</summary>
    public void MoveFocus(LibraryMove move, int pageRows, bool ctrl, bool shift)
    {
        var rows = _list.ShownRows;
        if (rows.Count == 0)
        {
            return;
        }

        var path = rows[Math.Clamp(TargetIndex(move, pageRows), 0, rows.Count - 1)].Path;
        if (shift)
        {
            SelectRange(path, add: ctrl);
        }
        else if (!ctrl)
        {
            SelectOnly(path);
        }

        _focus = path;
        Refresh();
    }

    /// <summary>Ctrl+A: the selection becomes every shown row.</summary>
    public void SelectAll()
    {
        _selected.Clear();
        _selected.UnionWith(_list.ShownRows.Select(row => row.Path));
        Refresh();
    }

    /// <summary>Ctrl+Space: toggles the focused row, which becomes the anchor.</summary>
    public void ToggleFocused()
    {
        if (FocusedRow is { } row)
        {
            Click(row, ctrl: true, shift: false);
        }
    }

    /// <summary>Esc, the ✕, or a click on empty space: nothing selected; the focus stays.</summary>
    public void Clear()
    {
        _selected.Clear();
        Refresh();
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
        else if (!Same(_focus, row.Path) && _list.IsShown(row.Path))
        {
            _focus = row.Path;
            Refresh();
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

    /// <summary>The App asked the engine to rename an item: the selection moves to the new path when it arrives.</summary>
    public void ExpectRename(string from, string to) => _renames[from] = to;

    /// <summary>The engine refused the rename.</summary>
    public void ForgetRename(string from) => _renames.Remove(from);

    /// <summary>After the list read a snapshot: follows the App's own renames and drops what left the Library.</summary>
    public void FollowSnapshot()
    {
        // A hidden item stays selected; only one that left the Library is dropped.
        CarryRenames();
        _selected.RemoveWhere(path => !_list.Contains(path));
        if (_anchor is not null && !_list.Contains(_anchor))
        {
            _anchor = null;
        }

        _lostFocus = _focus is not null && !_list.Contains(_focus) ? _focus : null;
        _shownBefore = _list.ShownRows;
    }

    /// <summary>After the list published the snapshot: a focus whose row left moves to a neighbour.</summary>
    public void RestoreFocus()
    {
        if (_lostFocus is not { } lost)
        {
            return;
        }

        _lostFocus = null;
        _focus = NextFocus(_shownBefore, lost);
        Refresh();
    }

    /// <summary>Sets each row's flags from the selection, then tells the command bar and the commands.</summary>
    public void Refresh()
    {
        _shownSelected = _list.ShownRows.Where(row => _selected.Contains(row.Path)).ToList();
        foreach (var row in _list.AllRows)
        {
            row.IsSelected = _selected.Contains(row.Path);
            row.IsFocused = Same(row.Path, _focus);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasShownSelection));
        OnPropertyChanged(nameof(HiddenText));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(FocusedRow));
        Changed?.Invoke();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private int TargetIndex(LibraryMove move, int pageRows)
    {
        var current = FocusedRow is { } focused ? _list.ShownIndexOf(focused.Path) : -1;
        var page = Math.Max(1, pageRows);
        return move switch
        {
            LibraryMove.Up => current < 0 ? 0 : current - 1,
            LibraryMove.Down => current < 0 ? 0 : current + 1,
            LibraryMove.PageUp => current < 0 ? 0 : current - page,
            LibraryMove.PageDown => current < 0 ? 0 : current + page,
            LibraryMove.Home => 0,
            _ => _list.ShownRows.Count - 1,
        };
    }

    private void SelectOnly(string path)
    {
        _selected.Clear();
        _selected.Add(path);
        _anchor = path;
    }

    private void Toggle(string path)
    {
        if (!_selected.Remove(path))
        {
            _selected.Add(path);
        }

        _anchor = path;
    }

    // Selects (or adds) the shown rows from the anchor to path; a missing anchor becomes path.
    private void SelectRange(string path, bool add)
    {
        if (_anchor is null || !_list.IsShown(_anchor))
        {
            _anchor = path;
        }

        var (from, to) = (_list.ShownIndexOf(_anchor), _list.ShownIndexOf(path));
        if (!add)
        {
            _selected.Clear();
        }

        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _selected.Add(_list.ShownRows[i].Path);
        }
    }

    // A pending rename whose new path has arrived moves the selection, anchor and focus there.
    private void CarryRenames()
    {
        foreach (var (from, to) in _renames.ToList())
        {
            if (_list.Contains(from))
            {
                continue;
            }

            _renames.Remove(from);
            if (_list.Find(to) is not { } renamed)
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
    private string? NextFocus(IReadOnlyList<LibraryRow> previous, string lost)
    {
        var index = -1;
        for (var i = 0; i < previous.Count && index < 0; i++)
        {
            index = Same(previous[i].Path, lost) ? i : -1;
        }

        if (index < 0)
        {
            return null;
        }

        for (var i = index + 1; i < previous.Count; i++)
        {
            if (_list.IsShown(previous[i].Path))
            {
                return previous[i].Path;
            }
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (_list.IsShown(previous[i].Path))
            {
                return previous[i].Path;
            }
        }

        return null;
    }
}
