using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>
/// The document's Game Profiles (story 3.6): each one's name, Ignored and Recordings, every edit sent at
/// once as one profile op. Add hands an executable to <c>game.add</c>, which writes <c>manualGames</c>.
/// "Overrides for" hosts a profile-scoped Replay Buffer page and Track Layout page for the selected
/// profile (story 4.11), whose notes show as this page's <see cref="SettingsPage.Message"/>.
/// Members run on the UI thread.
/// </summary>
internal sealed class GameProfilesPage : SettingsPage
{
    private readonly SettingsSender _sender;
    private readonly IEngineSession _engine;
    private string _addPath = "";
    private GameProfileItem? _selectedProfile;
    private TrackLayoutPage? _overrideTrackLayout;
    private ReplayBufferSettingsPage? _overrideOutput;
    private StateSnapshot? _state;
    private bool _isOpen;

    public GameProfilesPage(SettingsSender sender, IEngineSession engine)
        : base(sender)
    {
        _sender = sender;
        _engine = engine;
        AddCommand = new RelayCommand(() => _ = AddAsync());
    }

    public override string Title => "Game Profiles";

    /// <summary>The document's profiles, by name (ordinal ignore-case).</summary>
    public ObservableCollection<GameProfileItem> Profiles { get; } = [];

    /// <summary>The executable path Add sends, typed or browsed.</summary>
    public string AddPath
    {
        get => _addPath;
        set => Set(ref _addPath, value ?? "");
    }

    public ICommand AddCommand { get; }

    /// <summary>The profile whose overrides the hosted editors show; null hosts none.</summary>
    public GameProfileItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(value, _selectedProfile))
            {
                return;
            }

            if (_overrideTrackLayout is { } oldLayout)
            {
                oldLayout.PropertyChanged -= ChildChanged;
                oldLayout.IsOpen = false;
            }

            if (_overrideOutput is { } oldOutput)
            {
                oldOutput.PropertyChanged -= ChildChanged;
            }

            _selectedProfile = value;
            TrackLayoutPage? layout = null;
            ReplayBufferSettingsPage? output = null;
            if (value is not null)
            {
                layout = new TrackLayoutPage(_sender, _engine, value.ProfileId);
                output = new ReplayBufferSettingsPage(_sender, value.ProfileId);
                if (_state is { } state)
                {
                    layout.ShowDevices(state.Devices);
                    layout.ShowMeters(state.Meters, state.EngineInstanceId);
                    output.ShowState(state);
                }

                // A new page shows nothing until rendered, and Render skips a revision already shown.
                layout.Render(Document);
                output.Render(Document);
                layout.PropertyChanged += ChildChanged;
                output.PropertyChanged += ChildChanged;
                layout.IsOpen = _isOpen;
            }

            _overrideTrackLayout = layout;
            _overrideOutput = output;
            Message = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedProfile));
            OnPropertyChanged(nameof(OverrideTrackLayout));
            OnPropertyChanged(nameof(OverrideOutput));
        }
    }

    public bool HasSelectedProfile => _selectedProfile is not null;

    /// <summary>The selected profile's Track Layout override editor.</summary>
    public TrackLayoutPage? OverrideTrackLayout => _overrideTrackLayout;

    /// <summary>The selected profile's Buffer Window and Output Settings override editor.</summary>
    public ReplayBufferSettingsPage? OverrideOutput => _overrideOutput;

    /// <summary>Whether the page is on screen; the hosted Track Layout editor meters only then.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value) && _overrideTrackLayout is { } layout)
            {
                layout.IsOpen = value;
            }
        }
    }

    /// <summary>Takes the snapshot's devices, meters and video options for the hosted editors. Called before <see cref="SettingsPage.Render"/>.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _overrideTrackLayout?.ShowDevices(state.Devices);
        _overrideTrackLayout?.ShowMeters(state.Meters, state.EngineInstanceId);
        _overrideOutput?.ShowState(state);
    }

    protected override FrameworkElement CreateView() => new GameProfilesView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        var existing = Profiles.ToDictionary(p => p.ProfileId, StringComparer.Ordinal);
        var ordered = document.Profiles
            .OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase)
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
                item = new GameProfileItem(id, this);
                Profiles.Insert(index, item);
            }
            else if (Profiles.IndexOf(item) != index)
            {
                Profiles.Move(Profiles.IndexOf(item), index);
            }

            item.Show(profile);
        }

        if (_selectedProfile is { } selected && !document.Profiles.ContainsKey(selected.ProfileId))
        {
            SelectedProfile = null;
        }

        _overrideOutput?.Render(document);
        _overrideTrackLayout?.Render(document);
    }

    /// <summary>Sends one attribute of one profile against the rendered revision.</summary>
    internal void Send(string profileId, string attribute, JsonElement value) =>
        _ = SendAsync([SettingsOp.SetProfile(profileId, attribute, value)]);

    private void ChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Message) && sender is SettingsPage page)
        {
            Message = page.Message;
        }
    }

    private async Task AddAsync()
    {
        try
        {
            await _engine.SendAsync(GameMessages.Add, new GameAdd(AddPath));
            AddPath = "";
            Message = "Added. The game appears in this list once it is detected.";
        }
        catch (ErrorCodeException e)
        {
            Message = $"Not added ({e.Code}): {e.Message}";
        }
    }
}

/// <summary>One Game Profile. Its setters send the edit; <see cref="Show"/> renders the document's values silently.</summary>
internal sealed class GameProfileItem(string profileId, GameProfilesPage page) : Observable
{
    private string _name = "";
    private bool _ignored;
    private bool _recordingsEnabled;

    /// <summary>The profile id: the executable's canonical path (AD-18).</summary>
    public string ProfileId { get; } = profileId;

    /// <summary>A non-blank name that differs from the rendered one is sent trimmed; a blank one shows the old name again.</summary>
    public string Name
    {
        get => _name;
        set
        {
            var name = value?.Trim() ?? "";
            if (name.Length == 0 || name == _name)
            {
                OnPropertyChanged();
                return;
            }

            _name = name;
            OnPropertyChanged();
            page.Send(ProfileId, ProfileAttributes.Name, JsonSerializer.SerializeToElement(name, SettingsJsonContext.Default.String));
        }
    }

    public bool Ignored
    {
        get => _ignored;
        set => SendFlag(ref _ignored, value, ProfileAttributes.Ignored);
    }

    public bool RecordingsEnabled
    {
        get => _recordingsEnabled;
        set => SendFlag(ref _recordingsEnabled, value, ProfileAttributes.RecordingsEnabled);
    }

    /// <summary>Raises only for values that changed, so another profile's commit doesn't reset a name being typed.</summary>
    public void Show(GameProfile profile)
    {
        Set(ref _name, profile.Name, nameof(Name));
        Set(ref _ignored, profile.Ignored, nameof(Ignored));
        Set(ref _recordingsEnabled, profile.RecordingsEnabled, nameof(RecordingsEnabled));
    }

    // Sets a flag and, when it changed, sends it as the profile's attribute.
    private void SendFlag(ref bool field, bool value, string attribute, [CallerMemberName] string? property = null)
    {
        if (Set(ref field, value, property))
        {
            page.Send(ProfileId, attribute, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Boolean));
        }
    }
}
