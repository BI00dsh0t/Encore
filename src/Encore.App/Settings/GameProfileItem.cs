using System.Windows.Media;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A row of the Games list. It holds no profile id the user can see; the page shows the program's location.</summary>
internal sealed class GameProfileItem(string profileId) : Observable
{
    private string _name = "";
    private bool _ignored;
    private bool _isManual;
    private string? _source;
    private int _changed;
    private bool _isCapturing;
    private bool _unsaved;
    private ImageSource? _icon;

    /// <summary>The profile id: the program's canonical path; never shown in the list.</summary>
    public string ProfileId { get; } = profileId;

    public string Name => _name;

    /// <summary>The letter of the generic tile shown until, or instead of, the program's icon.</summary>
    public string Initial => _name.Length > 0 ? char.ToUpperInvariant(_name[0]).ToString() : "?";

    public bool Ignored => _ignored;

    /// <summary>The group: the games, then Ignored games.</summary>
    public string Group => _ignored ? "Ignored games" : "";

    public bool IsManual => _isManual;

    /// <summary>How Encore knows the game ("Steam game", "Added by you"…), then "· {n} settings changed".</summary>
    public string Caption =>
        GameSourceText.Describe(_source, _isManual)
        + (_changed switch { 0 => "", 1 => " · 1 setting changed", var n => $" · {n} settings changed" });

    /// <summary>How many of the game's three overridable settings it changes.</summary>
    public int ChangedCount => _changed;

    /// <summary>The Capturing now pill: the snapshot's game being captured.</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        set => Set(ref _isCapturing, value);
    }

    /// <summary>The unsaved dot: the game's page holds a draft.</summary>
    public bool HasUnsavedChanges
    {
        get => _unsaved;
        set => Set(ref _unsaved, value);
    }

    /// <summary>The program's icon from the shell, or null for the letter tile.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Renders the document's values; raises only what changed.</summary>
    public void Show(GameProfile profile, bool isManual, int changed)
    {
        if (Set(ref _name, profile.Name, nameof(Name)))
        {
            OnPropertyChanged(nameof(Initial));
        }

        if (Set(ref _ignored, profile.Ignored, nameof(Ignored)))
        {
            OnPropertyChanged(nameof(Group));
        }

        var captionChanged = Set(ref _isManual, isManual, nameof(IsManual));
        captionChanged |= Set(ref _source, profile.Source, nameof(Caption));
        captionChanged |= Set(ref _changed, changed, nameof(ChangedCount));
        if (captionChanged)
        {
            OnPropertyChanged(nameof(Caption));
        }
    }
}
