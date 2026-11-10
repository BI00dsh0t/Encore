using System.Windows;
using System.Windows.Input;

namespace Encore.App.Settings;

/// <summary>One of a game's own settings, shown as inherited or changed, with buttons to change it or reset it.</summary>
internal sealed class InheritedSetting : Observable
{
    private readonly Func<FrameworkElement> _editor;
    private bool _isChanged;
    private string _summary = "";
    private FrameworkElement? _view;

    public InheritedSetting(string title, Action change, Action reset, Action editMain, Func<FrameworkElement> editor)
    {
        Title = title;
        _editor = editor;
        ChangeCommand = new RelayCommand(change);
        ResetCommand = new RelayCommand(reset);
        EditMainCommand = new RelayCommand(editMain);
    }

    public string Title { get; }

    /// <summary>Whether the game has its own value, even one equal to the main one.</summary>
    public bool IsChanged
    {
        get => _isChanged;
        private set
        {
            if (Set(ref _isChanged, value))
            {
                OnPropertyChanged(nameof(UsesMain));
                OnPropertyChanged(nameof(Chip));
            }
        }
    }

    public bool UsesMain => !IsChanged;

    public string Chip => IsChanged ? "Changed for this game" : "Uses the main setting";

    /// <summary>The main value as text, shown while the game uses it.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>The editor, built on first use.</summary>
    public FrameworkElement Editor => _view ??= _editor();

    public ICommand ChangeCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand EditMainCommand { get; }

    public void Show(bool isChanged, string summary)
    {
        IsChanged = isChanged;
        Summary = summary;
    }
}
