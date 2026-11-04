using System.Windows;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>A device the Feedback Sounds page offers; a null id is the default communications device.</summary>
internal sealed record FeedbackDeviceItem(string? EndpointId, string Label);

/// <summary>
/// The Feedback Sounds page (story 5.11): a switch and a Play button per sound, the sound device, and the
/// active <c>feedback-device-captured</c> warning. Each edit is one Global op; Play sends
/// <c>feedback.preview</c>, and the engine plays it (AD-15). Members run on the UI thread.
/// </summary>
internal sealed class FeedbackSoundsPage : SettingsPage
{
    private const string DefaultDeviceLabel = "Default communications device";

    private readonly IEngineSession _engine;
    private FeedbackSounds _sounds = Keys.FeedbackSounds.Default;
    private string? _device = Keys.FeedbackSoundDevice.Default;
    private AudioDeviceList _devices = AudioDeviceList.Empty;
    private IReadOnlyList<FeedbackDeviceItem> _deviceItems = [new(null, DefaultDeviceLabel)];
    private string? _capturedWarning;

    public FeedbackSoundsPage(SettingsSender sender, IEngineSession engine)
        : base(sender)
    {
        _engine = engine;
        Sounds =
        [
            new(this, FeedbackSound.ClipSaved, "Clip saved"),
            new(this, FeedbackSound.SaveFailed, "Save failed"),
            new(this, FeedbackSound.RecordingStarted, "Recording started"),
            new(this, FeedbackSound.RecordingStopped, "Recording stopped"),
            new(this, FeedbackSound.DiskFull, "Disk full"),
        ];
    }

    public override string Title => "Feedback Sounds";

    /// <summary>One row per sound, in the page's order.</summary>
    public IReadOnlyList<FeedbackSoundRow> Sounds { get; }

    /// <summary>The default device first, then the available Output Devices, then the set device if it isn't listed.</summary>
    public IReadOnlyList<FeedbackDeviceItem> Devices
    {
        get => _deviceItems;
        private set => Set(ref _deviceItems, value);
    }

    public FeedbackDeviceItem? SelectedDevice
    {
        get => _deviceItems.FirstOrDefault(d => SameEndpoint(d.EndpointId, _device));
        set
        {
            // A combo box whose list changed clears its selection; that isn't a choice.
            if (value is null || SameEndpoint(value.EndpointId, _device))
            {
                return;
            }

            _device = value.EndpointId;
            OnPropertyChanged();
            _ = SendAsync([SettingsValues.SetGlobal(Keys.FeedbackSoundDevice, value.EndpointId)]);
        }
    }

    /// <summary>The active <c>feedback-device-captured</c> warning's message, or null.</summary>
    public string? CapturedWarning
    {
        get => _capturedWarning;
        private set => Set(ref _capturedWarning, value);
    }

    /// <summary>Takes the snapshot's devices and warnings. Called before <see cref="SettingsPage.Render"/>.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // A session that isn't connected has no active warnings, as on the status line.
        CapturedWarning = EngineSessions.ConnectedState(_engine.Status, state)?.Warnings.FirstOrDefault(w => w.Code == ErrorCodes.FeedbackDeviceCaptured)?.Message;
        if (!Equals(_devices, state.Devices))
        {
            _devices = state.Devices;
            ShowDevices();
        }
    }

    internal bool IsOn(FeedbackSound sound) => sound switch
    {
        FeedbackSound.ClipSaved => _sounds.ClipSaved,
        FeedbackSound.SaveFailed => _sounds.SaveFailed,
        FeedbackSound.RecordingStarted => _sounds.RecordingStarted,
        FeedbackSound.RecordingStopped => _sounds.RecordingStopped,
        _ => _sounds.DiskFull,
    };

    internal void Switch(FeedbackSoundRow row, bool value)
    {
        if (IsOn(row.Sound) == value)
        {
            return;
        }

        _sounds = row.Sound switch
        {
            FeedbackSound.ClipSaved => _sounds with { ClipSaved = value },
            FeedbackSound.SaveFailed => _sounds with { SaveFailed = value },
            FeedbackSound.RecordingStarted => _sounds with { RecordingStarted = value },
            FeedbackSound.RecordingStopped => _sounds with { RecordingStopped = value },
            _ => _sounds with { DiskFull = value },
        };
        row.Changed();
        _ = SendAsync([SettingsValues.SetGlobal(Keys.FeedbackSounds, _sounds)]);
    }

    internal async Task PlayAsync(FeedbackSound sound)
    {
        try
        {
            await _engine.SendAsync(FeedbackMessages.Preview, new FeedbackPreview(sound));
            Message = null;
        }
        catch (ErrorCodeException e)
        {
            Message = $"Couldn't play ({e.Code}): {e.Message}";
        }
    }

    protected override FrameworkElement CreateView() => new FeedbackSoundsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _sounds = SettingsValues.Global(document, Keys.FeedbackSounds);
        _device = SettingsValues.Global(document, Keys.FeedbackSoundDevice);
        foreach (var row in Sounds)
        {
            row.Changed();
        }

        ShowDevices();
    }

    private static bool SameEndpoint(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void ShowDevices()
    {
        var outputs = _devices.Devices.Where(d => d.Flow == AudioFlow.Output && d.Available).ToList();
        bool Shared(string name) => outputs.Count(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) > 1;

        List<FeedbackDeviceItem> items = [new(null, DefaultDeviceLabel)];
        items.AddRange(outputs.Select(d => new FeedbackDeviceItem(
            d.EndpointId,
            Shared(d.FriendlyName) && d.ParentName.Length > 0 ? $"{d.FriendlyName} — {d.ParentName}" : d.FriendlyName)));
        if (_device is { } id && !items.Any(i => SameEndpoint(i.EndpointId, id)))
        {
            var known = _devices.Devices.FirstOrDefault(d => SameEndpoint(d.EndpointId, id));
            items.Add(new(id, (known?.FriendlyName is { Length: > 0 } name ? name : id) + " (not connected)"));
        }

        // The selection follows the list, so a combo box that cleared its selection gets it back.
        if (!items.SequenceEqual(_deviceItems))
        {
            Devices = items;
        }

        OnPropertyChanged(nameof(SelectedDevice));
    }
}

/// <summary>One sound on the Feedback Sounds page: its switch and its Play button.</summary>
internal sealed class FeedbackSoundRow : Observable
{
    private readonly FeedbackSoundsPage _page;

    public FeedbackSoundRow(FeedbackSoundsPage page, FeedbackSound sound, string label)
    {
        _page = page;
        Sound = sound;
        Label = label;
        PlayCommand = new RelayCommand(() => _ = page.PlayAsync(sound));
    }

    public FeedbackSound Sound { get; }

    public string Label { get; }

    /// <summary>The sound's <c>feedbackSounds</c> flag; setting it sends the whole value with that flag changed.</summary>
    public bool IsOn
    {
        get => _page.IsOn(Sound);
        set => _page.Switch(this, value);
    }

    /// <summary>Sends <c>feedback.preview</c> for the sound, whether or not it is switched on.</summary>
    public ICommand PlayCommand { get; }

    internal void Changed() => OnPropertyChanged(nameof(IsOn));
}
