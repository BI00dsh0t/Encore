using System.Globalization;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Monitors;

namespace ReplayBuffer.App.Settings;

/// <summary>
/// The screens a Monitor Recording composites (story 11.4, design-multi-monitor.md's picker), beside the
/// Monitor Recording key on Hotkeys. It replaces story 10.9's one-screen combo box:
/// <list type="bullet">
/// <item>All screens, or Chosen screens with one <see cref="ScreenRow"/> per screen, at least one kept ticked;</item>
/// <item>a layout preview of <see cref="ScreenTile"/>s at the screens' positions, hidden when the engine
/// reports none, each toggling the same selection;</item>
/// <item>the output line from the canvas rule (AD-25), and chosen screens that aren't attached, greyed.</item>
/// </list>
/// Each choice is one Global <c>monitorRecordingScreens</c> op, sent through the hosting page; the App never
/// writes <c>monitorRecordingMonitor</c>, which is only read while the new key is null. The list comes from
/// <c>monitors.list</c> per engine run and follows <c>monitors.changed</c>. Members run on the UI thread.
/// </summary>
internal sealed class MonitorPicker : Observable
{
    /// <summary>The preview's largest size, in device-independent pixels.</summary>
    public const double PreviewMaxWidth = 420;

    public const double PreviewMaxHeight = 130;

    private const double TileGap = 2;

    private readonly IEngineSession _engine;
    private readonly Func<IReadOnlyList<SettingsOp>, Task> _send;
    private readonly Action<string?> _message;
    private readonly Dictionary<string, DisplayMonitor> _known = new(StringComparer.OrdinalIgnoreCase);
    private MonitorRecordingScreens? _screens = Keys.MonitorRecordingScreens.Default;
    private string? _legacy = Keys.MonitorRecordingMonitor.Default;
    private IReadOnlyList<string> _lastChosen = [];
    private VideoCodec _codec = Keys.Video.Default.Codec;
    private VideoOptions _options = VideoOptions.Unprobed;
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private IReadOnlyList<ScreenRow> _rows = [];
    private IReadOnlyList<ScreenTile> _tiles = [];
    private string _outputText = "";
    private double _previewWidth;
    private double _previewHeight;
    private string? _fetchedInstance;
    private MonitorList? _changed;

    /// <param name="send">Sends the ops, as the hosting page's <c>SendAsync</c>.</param>
    /// <param name="message">Shows a note on the hosting page's error bar; null clears it.</param>
    public MonitorPicker(IEngineSession engine, Func<IReadOnlyList<SettingsOp>, Task> send, Action<string?> message)
    {
        _engine = engine;
        _send = send;
        _message = message;
        RefreshCommand = new RelayCommand(() => _ = FetchAsync());
    }

    /// <summary>"All screens": every attached screen, as they come and go between Recordings.</summary>
    public bool IsAllScreens
    {
        get => _screens?.Mode == MonitorScreensMode.All;
        set
        {
            if (value && !IsAllScreens)
            {
                _lastChosen = ChosenIds();
                Choose(MonitorRecordingScreens.AllScreens);
            }
        }
    }

    /// <summary>"Chosen screens": the ticked ones. An unset key shows the screen the old setting records.</summary>
    public bool IsChosenScreens
    {
        get => !IsAllScreens;
        set
        {
            if (!value || !IsAllScreens)
            {
                return;
            }

            // The set chosen before All screens, else every attached screen, as All showed them.
            IReadOnlyList<string> ids = _lastChosen.Count > 0 ? _lastChosen : [.. _monitors.Select(Id)];
            if (ids.Count > 0)
            {
                Choose(MonitorRecordingScreens.Selected(ids));
            }
        }
    }

    /// <summary>One row per attached screen in <c>monitors.list</c>'s order, then the chosen screens that aren't attached.</summary>
    public IReadOnlyList<ScreenRow> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    /// <summary>The layout preview's screens; empty when <see cref="HasPreview"/> is false.</summary>
    public IReadOnlyList<ScreenTile> Tiles
    {
        get => _tiles;
        private set => Set(ref _tiles, value);
    }

    /// <summary>Every attached screen has a position, so the preview can be drawn.</summary>
    public bool HasPreview => _tiles.Count > 0;

    public double PreviewWidth
    {
        get => _previewWidth;
        private set => Set(ref _previewWidth, value);
    }

    public double PreviewHeight
    {
        get => _previewHeight;
        private set => Set(ref _previewHeight, value);
    }

    /// <summary>The video the chosen screens make, from the canvas rule: "Recorded as one 5120 × 1440 video."</summary>
    public string OutputText
    {
        get => _outputText;
        private set => Set(ref _outputText, value);
    }

    /// <summary>Lists the screens again.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// Fetches the screens from a newly connected engine run, shows the list a <c>monitors.changed</c>
    /// carries (T-03), and takes the encoder probe's codecs. Called before the hosting page renders.
    /// </summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var optionsChanged = !Equals(_options, state.VideoOptions);
        _options = state.VideoOptions;
        if (EngineSessions.ConnectedState(_engine.Status, state) is { } connected)
        {
            if (connected.EngineInstanceId != _fetchedInstance)
            {
                _fetchedInstance = connected.EngineInstanceId;
                _changed = connected.Monitors;
                _ = FetchAsync();
            }
            else if (connected.Monitors is { } changed && !ReferenceEquals(changed, _changed))
            {
                _changed = changed;
                ShowMonitors(changed.Monitors);
                return;
            }
        }

        if (optionsChanged)
        {
            Show();
        }
    }

    /// <summary>Shows the document's <c>monitorRecordingScreens</c> (else the old key's screen) and the Global codec.</summary>
    public void Render(SettingsDocument document)
    {
        _screens = SettingsValues.Global(document, Keys.MonitorRecordingScreens);
        _legacy = SettingsValues.Global(document, Keys.MonitorRecordingMonitor);
        _codec = SettingsValues.Global(document, Keys.Video).Codec;
        if (_screens is { Mode: MonitorScreensMode.Selected, Monitors: { Count: > 0 } chosen })
        {
            _lastChosen = chosen;
        }

        Show();
    }

    /// <summary>
    /// Ticks or unticks <paramref name="row"/>'s screen under Chosen screens and sends the selection.
    /// Unticking the last chosen screen is refused, and the row shows ticked again.
    /// </summary>
    internal void Toggle(ScreenRow row, bool chosen)
    {
        var ids = ChosenIds().ToList();
        var has = ids.Any(id => Matches(row, id));
        if (IsAllScreens || chosen == has || (!chosen && ids.Count == 1))
        {
            row.Refresh();
            return;
        }

        if (chosen)
        {
            ids.Add(row.Id);
        }
        else
        {
            ids.RemoveAll(id => Matches(row, id));
        }

        // Attached screens in the list's order, then the ones that aren't attached.
        var ordered = _monitors.Select(Id).Where(ids.Contains).Concat(ids.Where(id => !_monitors.Any(m => Same(Id(m), id)))).ToList();
        _lastChosen = ordered;
        Choose(MonitorRecordingScreens.Selected(ordered));
    }

    /// <summary>The id a selection stores for a screen: its DisplayConfig path, else (an older engine) its device name.</summary>
    private static string Id(DisplayMonitor monitor) => monitor.MonitorId ?? monitor.DeviceName;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Core's resolver rule: a saved id names a screen by its MonitorId, else by its device name.
    private static bool Matches(DisplayMonitor monitor, string id) => (monitor.MonitorId is { } monitorId && Same(monitorId, id)) || Same(monitor.DeviceName, id);

    private static bool Matches(ScreenRow row, string id) => row.Monitor is { } monitor ? Matches(monitor, id) : Same(row.Id, id);

    /// <summary>The number Windows shows: 2 for <c>\\.\DISPLAY2</c>; else <paramref name="fallback"/>.</summary>
    private static string Number(string deviceName, int fallback)
    {
        var at = deviceName.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && int.TryParse(deviceName.AsSpan(at + 7), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : fallback.ToString(CultureInfo.InvariantCulture);
    }

    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => "H.264",
    };

    private void Choose(MonitorRecordingScreens screens)
    {
        _screens = screens;
        Show();
        _ = _send([SettingsValues.SetGlobal(Keys.MonitorRecordingScreens, screens)]);
    }

    /// <summary>
    /// The chosen ids under Chosen screens. An unset key is the screen the old setting records: its device
    /// name's screen, else the primary.
    /// </summary>
    private IReadOnlyList<string> ChosenIds()
    {
        if (_screens is { Mode: MonitorScreensMode.Selected, Monitors: { } ids })
        {
            // An attached screen's id as it is now, so a value from the old key or an older engine catches up.
            return [.. ids.Select(id => _monitors.FirstOrDefault(m => Matches(m, id)) is { } m ? Id(m) : id).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        if (_screens is null)
        {
            var legacy = (_legacy is null ? null : _monitors.FirstOrDefault(m => Same(m.DeviceName, _legacy)))
                ?? _monitors.FirstOrDefault(m => m.Primary)
                ?? _monitors.FirstOrDefault();
            return legacy is null ? [] : [Id(legacy)];
        }

        return [];
    }

    private async Task FetchAsync()
    {
        try
        {
            var monitors = (await _engine.SendAsync(RecordingMessages.MonitorsList, Empty.Value)).Monitors;
            _message(null);
            ShowMonitors(monitors);
        }
        catch (ErrorCodeException e)
        {
            _message($"Couldn't list screens ({e.Code}): {e.Message}");
        }
    }

    private void ShowMonitors(IReadOnlyList<DisplayMonitor> monitors)
    {
        _monitors = monitors;
        foreach (var monitor in monitors)
        {
            _known[Id(monitor)] = monitor;
        }

        Show();
    }

    private void Show()
    {
        var all = IsAllScreens;
        var chosen = all ? [] : ChosenIds();
        var lastOne = chosen.Count == 1;

        // Attached screens, then chosen ones that aren't attached (by the name they were last seen with).
        List<(string Id, DisplayMonitor? Monitor, string Label, bool Chosen)> wanted = [];
        for (var i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            var label = $"{Number(m.DeviceName, i + 1)}  {m.FriendlyName} · {m.Width} × {m.Height}"
                + (m.RefreshHz is { } hz ? $" · {hz} Hz" : "")
                + (m.Primary ? " · primary" : "")
                + (m.Hdr ? " · HDR, recorded in SDR" : "");
            wanted.Add((Id(m), m, label, all || chosen.Any(id => Matches(m, id))));
        }

        foreach (var id in chosen.Where(id => !_monitors.Any(m => Matches(m, id))))
        {
            var name = _known.Values.FirstOrDefault(k => Matches(k, id)) is { } seen
                ? $"{Number(seen.DeviceName, wanted.Count + 1)}  {seen.FriendlyName}"
                : "Unknown screen";
            wanted.Add((id, null, $"{name} · Not connected — skipped", true));
        }

        // Rows keep their identity while the list is the same, so a focused check box or tile keeps focus.
        var rows = wanted.Select(w => w.Id).SequenceEqual(_rows.Select(r => r.Id), StringComparer.OrdinalIgnoreCase)
            ? _rows
            : [.. wanted.Select(w => new ScreenRow(this, w.Id))];
        for (var i = 0; i < rows.Count; i++)
        {
            var (_, monitor, label, isChosen) = wanted[i];
            rows[i].Show(monitor, label, isChosen, enabled: !all, lastOne && isChosen && !all ? "At least one screen stays chosen. Tick another screen first." : null);
        }

        Rows = rows;
        ShowPreview();
        OutputText = Output(all ? _monitors : [.. _monitors.Where(m => chosen.Any(id => Matches(m, id)))], chosen.Count);
        OnPropertyChanged(nameof(IsAllScreens));
        OnPropertyChanged(nameof(IsChosenScreens));
    }

    /// <summary>The attached screens at their virtual-desktop positions, scaled to fit the preview.</summary>
    private void ShowPreview()
    {
        if (_monitors.Count == 0 || _monitors.Any(m => m.X is null || m.Y is null))
        {
            if (_tiles.Count > 0)
            {
                Tiles = [];
                OnPropertyChanged(nameof(HasPreview));
            }

            return;
        }

        var left = _monitors.Min(m => m.X!.Value);
        var top = _monitors.Min(m => m.Y!.Value);
        var width = _monitors.Max(m => m.X!.Value + m.Width) - left;
        var height = _monitors.Max(m => m.Y!.Value + m.Height) - top;
        var scale = Math.Min(PreviewMaxWidth / width, PreviewMaxHeight / height);
        List<ScreenTile> tiles = [];
        for (var i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            tiles.Add(new ScreenTile(
                _rows[i],
                Number(m.DeviceName, i + 1),
                m.Primary,
                ((m.X!.Value - left) * scale) + TileGap,
                ((m.Y!.Value - top) * scale) + TileGap,
                Math.Max(1, (m.Width * scale) - (2 * TileGap)),
                Math.Max(1, (m.Height * scale) - (2 * TileGap))));
        }

        if (!tiles.SequenceEqual(_tiles))
        {
            Tiles = tiles;
            OnPropertyChanged(nameof(HasPreview));
        }

        PreviewWidth = width * scale;
        PreviewHeight = height * scale;
    }

    /// <summary>The output line: the canvas rule's size, "(scaled down)", and which format keeps the detail.</summary>
    private string Output(IReadOnlyList<DisplayMonitor> screens, int chosen)
    {
        if (screens.Count == 0)
        {
            return chosen > 0
                ? "None of the chosen screens is connected, so a Monitor Recording won't start."
                : "No screens found. Press Refresh to look again.";
        }

        // Before the probe, HEVC and AV1 are taken as available, as they are on most graphics cards.
        bool CanEncode(VideoCodec codec) => !_options.Probed || _options.Codecs.Any(c => c.Codec == codec);
        var plan = MonitorCanvasRule.Plan(screens, _codec, CanEncode);
        var text = $"Recorded as one {plan.Width} × {plan.Height} video{(plan.Scaled ? " (scaled down)" : "")}.";
        if (plan.Codec != _codec)
        {
            text += plan.Scaled
                ? $" It's saved as {CodecName(plan.Codec)}."
                : $" It's saved as {CodecName(plan.Codec)}, so every screen keeps its full detail.";
        }
        else if (plan.Scaled && plan.Codec == VideoCodec.H264)
        {
            text += " HEVC would keep full detail, but your graphics card's video encoder can't make it.";
        }

        return text;
    }
}

/// <summary>One screen's row in the <see cref="MonitorPicker"/>: a check box with its number, name, mode, primary and HDR.</summary>
internal sealed class ScreenRow(MonitorPicker picker, string id) : Observable
{
    private string _label = "";
    private bool _isChosen;
    private bool _isEnabled = true;
    private string? _toolTip;
    private DisplayMonitor? _monitor;

    /// <summary>The id the selection stores for this screen.</summary>
    public string Id { get; } = id;

    /// <summary>The attached screen; null for a chosen screen that isn't attached.</summary>
    public DisplayMonitor? Monitor => _monitor;

    /// <summary>"1  Dell U2723QE · 2560 × 1440 · 144 Hz · primary", or "… · Not connected — skipped".</summary>
    public string Label
    {
        get => _label;
        private set => Set(ref _label, value);
    }

    /// <summary>The screen is attached; a chosen one that isn't shows greyed.</summary>
    public bool IsConnected => _monitor is not null;

    /// <summary>Ticked: recorded. Setting it ticks or unticks the screen through the picker.</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (value != _isChosen)
            {
                picker.Toggle(this, value);
            }
        }
    }

    /// <summary>Chosen screens: the builder can tick it. All screens shows every screen ticked and disabled.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set => Set(ref _isEnabled, value);
    }

    /// <summary>Why the last chosen screen can't be unticked; null otherwise.</summary>
    public string? ToolTip
    {
        get => _toolTip;
        private set => Set(ref _toolTip, value);
    }

    /// <summary>Shows <see cref="IsChosen"/> again, after a refused change.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsChosen));

    internal void Show(DisplayMonitor? monitor, string label, bool isChosen, bool enabled, string? toolTip)
    {
        if (!Equals(_monitor, monitor))
        {
            _monitor = monitor;
            OnPropertyChanged(nameof(Monitor));
            OnPropertyChanged(nameof(IsConnected));
        }

        Label = label;
        Set(ref _isChosen, isChosen, nameof(IsChosen));
        IsEnabled = enabled;
        ToolTip = toolTip;
    }
}

/// <summary>
/// One attached screen in the layout preview, in preview pixels, sharing <see cref="Row"/>'s selection.
/// A record, so an unchanged layout keeps its tiles.
/// </summary>
internal sealed record ScreenTile(ScreenRow Row, string Number, bool Primary, double Left, double Top, double Width, double Height);
