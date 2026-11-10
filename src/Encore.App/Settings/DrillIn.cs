using System.Windows.Input;

namespace Encore.App.Settings;

/// <summary>
/// The drill-in pattern (story 10.10, epic 10's EXPERIENCE.md "Drill-in pages"): a list page opens one
/// item's page in its place, and the shell's page header becomes a breadcrumb (Back, the parent's title as
/// a link, the item's name). It closes on Back, the parent link, Esc, Alt+Left, the mouse's Back button
/// or the parent's nav item; the shell routes those here. The parent keeps each item's view model, so a
/// draft survives going back. Games uses it first; a Library item page can reuse it unchanged.
/// </summary>
internal sealed class DrillIn : Observable
{
    private string? _itemId;
    private string _itemTitle = "";

    public DrillIn(string parentTitle)
    {
        ParentTitle = parentTitle;
        BackCommand = new RelayCommand(Close);
    }

    /// <summary>Raised after the item page closes, with its item id, so the list can focus that row.</summary>
    public event Action<string>? Closed;

    /// <summary>Raised after an item page opens, so its view can focus its first control.</summary>
    public event Action<string>? Opened;

    /// <summary>The parent page's title, shown as the breadcrumb's link.</summary>
    public string ParentTitle { get; }

    /// <summary>The Back button's name for screen readers: "Back to Games".</summary>
    public string BackName => $"Back to {ParentTitle}";

    /// <summary>The open item's id, or null on the list.</summary>
    public string? ItemId
    {
        get => _itemId;
        private set
        {
            if (Set(ref _itemId, value))
            {
                OnPropertyChanged(nameof(IsOpen));
            }
        }
    }

    /// <summary>The open item's name, the breadcrumb's last part.</summary>
    public string ItemTitle
    {
        get => _itemTitle;
        set => Set(ref _itemTitle, value ?? "");
    }

    public bool IsOpen => ItemId is not null;

    /// <summary>Back, and the parent link: closes the item page.</summary>
    public ICommand BackCommand { get; }

    public void Open(string itemId, string title)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ItemTitle = title;
        ItemId = itemId;
        Opened?.Invoke(itemId);
    }

    /// <summary>Back to the list; a no-op on the list.</summary>
    public void Close()
    {
        if (ItemId is not { } id)
        {
            return;
        }

        ItemId = null;
        Closed?.Invoke(id);
    }
}
