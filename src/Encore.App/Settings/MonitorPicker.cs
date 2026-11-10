using System.Globalization;
using System.Windows.Input;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Monitors;

namespace Encore.App.Settings;

/// <summary>The screens a Monitor Recording composites: all or chosen ones, with a layout preview. UI thread.</summary>
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
    private bool _listed;

    /// <summary>Send sends the ops through the hosting page; message shows a note on its error bar, and null clears it.</summary>
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
            else
            {
                // Nothing to choose yet: the radio buttons go back to All screens.
                OnPropertyChanged(nameof(IsAllScreens));
                OnPropertyChanged();
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

    /// <summary>Fetches the screens from a new engine run, follows <c>monitors.changed</c>, and takes the probe's codecs.</summary>
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

    /// <summary>Ticks or unticks a screen under Chosen screens and sends it; unticking the last one is refused.</summary>
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
        var attached = _monitors.Select(Id).Where(ids.Contains);
        var ordered = attached.Concat(ids.Where(id => !_monitors.Any(monitor => Same(Id(monitor), id)))).ToList();
        _lastChosen = ordered;
        Choose(MonitorRecordingScreens.Selected(ordered));
    }

    /// <summary>The id a selection stores for a screen: its DisplayConfig path, else (an older engine) its device name.</summary>
    private static string Id(DisplayMonitor monitor) => monitor.MonitorId ?? monitor.DeviceName;

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    // Core's resolver rule: a saved id names a screen by its MonitorId, else by its device name.
    private static bool Matches(DisplayMonitor monitor, string id) =>
        (monitor.MonitorId is { } monitorId && Same(monitorId, id)) || Same(monitor.DeviceName, id);

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

    // An attached screen's id as it is now, so a value from the old key or an older engine catches up.
    private string CurrentId(string id) => _monitors.FirstOrDefault(monitor => Matches(monitor, id)) is { } attached ? Id(attached) : id;

    private void Choose(MonitorRecordingScreens screens)
    {
        _screens = screens;
        Show();
        _ = _send([SettingsValues.SetGlobal(Keys.MonitorRecordingScreens, screens)]);
    }

    /// <summary>The chosen ids; with the key unset, the screen the older setting names, else the primary.</summary>
    private IReadOnlyList<string> ChosenIds()
    {
        if (_screens is { Mode: MonitorScreensMode.Selected, Monitors: { } ids })
        {
            return [.. ids.Select(CurrentId).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        if (_screens is null)
        {
            var legacy = (_legacy is null ? null : _monitors.FirstOrDefault(monitor => Same(monitor.DeviceName, _legacy)))
                ?? _monitors.FirstOrDefault(monitor => monitor.Primary)
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
        _listed = true;
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
        var wanted = WantedRows(all, chosen);

        // Rows keep their identity while the list is the same, so a focused check box or tile keeps focus.
        var rows = wanted.Select(want => want.Id).SequenceEqual(_rows.Select(row => row.Id), StringComparer.OrdinalIgnoreCase)
            ? _rows
            : [.. wanted.Select(want => new ScreenRow(this, want.Id))];
        var lastOne = chosen.Count == 1;
        for (var i = 0; i < rows.Count; i++)
        {
            var (_, monitor, label, isChosen) = wanted[i];
            var toolTip = lastOne && isChosen && !all ? "At least one screen stays chosen. Tick another screen first." : null;
            rows[i].Show(monitor, label, isChosen, enabled: !all, toolTip);
        }

        Rows = rows;
        ShowPreview();
        OutputText = Output(all ? _monitors : [.. _monitors.Where(monitor => chosen.Any(id => Matches(monitor, id)))], chosen.Count);
        OnPropertyChanged(nameof(IsAllScreens));
        OnPropertyChanged(nameof(IsChosenScreens));
    }

    // Attached screens, then chosen ones that aren't attached (by the name they were last seen with).
    private List<(string Id, DisplayMonitor? Monitor, string Label, bool Chosen)> WantedRows(bool all, IReadOnlyList<string> chosen)
    {
        List<(string Id, DisplayMonitor? Monitor, string Label, bool Chosen)> wanted = [];
        for (var i = 0; i < _monitors.Count; i++)
        {
            var monitor = _monitors[i];
            wanted.Add((Id(monitor), monitor, AttachedLabel(monitor, i + 1), all || chosen.Any(id => Matches(monitor, id))));
        }

        // Before the first list, no chosen screen is called not connected.
        foreach (var id in _listed ? chosen.Where(id => !_monitors.Any(monitor => Matches(monitor, id))) : [])
        {
            wanted.Add((id, null, $"{MissingName(id, wanted.Count + 1)} · Not connected — skipped", true));
        }

        return wanted;
    }

    // "1  Dell U2723QE · 2560 × 1440 · 144 Hz · primary"
    private static string AttachedLabel(DisplayMonitor monitor, int position) =>
        $"{Number(monitor.DeviceName, position)}  {monitor.FriendlyName} · {monitor.Width} × {monitor.Height}"
        + (monitor.RefreshHz is { } hertz ? $" · {hertz} Hz" : "")
        + (monitor.Primary ? " · primary" : "")
        + (monitor.Hdr ? " · HDR, recorded in SDR" : "");

    // The name a chosen screen had when it was last attached.
    private string MissingName(string id, int position) =>
        _known.Values.FirstOrDefault(known => Matches(known, id)) is { } seen
            ? $"{Number(seen.DeviceName, position)}  {seen.FriendlyName}"
            : "Unknown screen";

    /// <summary>The attached screens at their virtual-desktop positions, scaled to fit the preview.</summary>
    private void ShowPreview()
    {
        if (_monitors.Count == 0 || _monitors.Any(monitor => monitor.X is null || monitor.Y is null))
        {
            if (_tiles.Count > 0)
            {
                Tiles = [];
                OnPropertyChanged(nameof(HasPreview));
            }

            return;
        }

        var left = _monitors.Min(monitor => monitor.X!.Value);
        var top = _monitors.Min(monitor => monitor.Y!.Value);
        var width = _monitors.Max(monitor => monitor.X!.Value + monitor.Width) - left;
        var height = _monitors.Max(monitor => monitor.Y!.Value + monitor.Height) - top;
        var scale = Math.Min(PreviewMaxWidth / width, PreviewMaxHeight / height);
        List<ScreenTile> tiles = [];
        for (var i = 0; i < _monitors.Count; i++)
        {
            var monitor = _monitors[i];
            tiles.Add(new ScreenTile(
                _rows[i],
                Number(monitor.DeviceName, i + 1),
                monitor.Primary,
                ((monitor.X!.Value - left) * scale) + TileGap,
                ((monitor.Y!.Value - top) * scale) + TileGap,
                Math.Max(1, (monitor.Width * scale) - (2 * TileGap)),
                Math.Max(1, (monitor.Height * scale) - (2 * TileGap))));
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
        if (!_listed)
        {
            return "Looking for your screens…";
        }

        if (screens.Count == 0)
        {
            return chosen > 0
                ? "None of the chosen screens is connected, so a Monitor Recording won't start."
                : "No screens found. Press Refresh to look again.";
        }

        // Before the probe, HEVC and AV1 are taken as available, as they are on most graphics cards.
        bool CanEncode(VideoCodec codec) => !_options.Probed || _options.Codecs.Any(option => option.Codec == codec);
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
