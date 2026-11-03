using System.Collections.ObjectModel;
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
/// Members run on the UI thread.
/// </summary>
internal sealed class GameProfilesPage : SettingsPage
{
    private readonly IEngineSession _engine;
    private string _addPath = "";

    public GameProfilesPage(SettingsSender sender, IEngineSession engine)
        : base(sender)
    {
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
    }

    /// <summary>Sends one attribute of one profile against the rendered revision.</summary>
    internal void Send(string profileId, string attribute, JsonElement value) =>
        _ = SendAsync([SettingsOp.SetProfile(profileId, attribute, value)]);

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
        set
        {
            if (Set(ref _ignored, value))
            {
                page.Send(ProfileId, ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Boolean));
            }
        }
    }

    public bool RecordingsEnabled
    {
        get => _recordingsEnabled;
        set
        {
            if (Set(ref _recordingsEnabled, value))
            {
                page.Send(ProfileId, ProfileAttributes.RecordingsEnabled, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Boolean));
            }
        }
    }

    /// <summary>Raises only for values that changed, so another profile's commit doesn't reset a name being typed.</summary>
    public void Show(GameProfile profile)
    {
        Set(ref _name, profile.Name, nameof(Name));
        Set(ref _ignored, profile.Ignored, nameof(Ignored));
        Set(ref _recordingsEnabled, profile.RecordingsEnabled, nameof(RecordingsEnabled));
    }
}
