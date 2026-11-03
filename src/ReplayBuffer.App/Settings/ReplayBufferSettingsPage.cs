using System.Globalization;
using System.IO;
using System.Windows;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>A codec the page offers, with its label.</summary>
internal sealed record CodecItem(VideoCodec Codec, string Label);

/// <summary>An output resolution the page offers, with its label.</summary>
internal sealed record ResolutionItem(int Width, int Height, string Label);

/// <summary>
/// The Global Replay Buffer and Output Settings (story 4.10): <c>bufferWindow</c>, <c>replayStorage</c>,
/// <c>ringPath</c>, <c>saveCooldown</c> and <c>video</c>, each edit sent at once as one Global op. An
/// invalid entry is not sent: the page shows the rendered value again with a <see cref="SettingsPage.Message"/>.
/// It shows the engine's storage estimate (R2) and warns, blocking nothing, when it is over the free
/// bytes. Members run on the UI thread.
/// </summary>
internal sealed class ReplayBufferSettingsPage(SettingsSender sender) : SettingsPage(sender)
{
    private int _bufferWindow = Keys.BufferWindow.Default;
    private ReplayStorage _storage = Keys.ReplayStorage.Default;
    private string? _ringPath = Keys.RingPath.Default;
    private int _saveCooldown = Keys.SaveCooldown.Default;
    private VideoSettings _video = Keys.Video.Default;
    private VideoOptions _options = VideoOptions.Unprobed;
    private StorageEstimate? _estimate;
    private IReadOnlyList<CodecItem> _codecs = [];
    private IReadOnlyList<ResolutionItem> _resolutions = [];

    public override string Title => "Replay Buffer";

    /// <summary><c>bufferWindow</c>, in s; at least 1.</summary>
    public string BufferWindowText
    {
        get => Text(_bufferWindow);
        set => SendInt(value, ref _bufferWindow, v => v >= 1, Keys.BufferWindow, "The Buffer Window must be a whole number of seconds, 1 or more.");
    }

    /// <summary><c>replayStorage</c> is Memory.</summary>
    public bool IsMemory
    {
        get => _storage == ReplayStorage.Memory;
        set
        {
            if (value)
            {
                SendStorage(ReplayStorage.Memory);
            }
        }
    }

    /// <summary><c>replayStorage</c> is Disk.</summary>
    public bool IsDisk
    {
        get => _storage == ReplayStorage.Disk;
        set => SendStorage(value ? ReplayStorage.Disk : ReplayStorage.Memory);
    }

    /// <summary><c>ringPath</c>; empty is the default folder.</summary>
    public string RingPath
    {
        get => _ringPath ?? "";
        set
        {
            var path = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (path is not null && !Path.IsPathFullyQualified(path))
            {
                Refuse("The ring folder must be a full path, such as D:\\ReplayBuffer\\ring, or empty for the default.", nameof(RingPath));
                return;
            }

            if (path == _ringPath)
            {
                OnPropertyChanged();
                return;
            }

            _ringPath = path;
            OnPropertyChanged();
            _ = SendAsync([SettingsValues.SetGlobal(Keys.RingPath, path)]);
        }
    }

    /// <summary><c>saveCooldown</c>, in s; 0 or more.</summary>
    public string SaveCooldownText
    {
        get => Text(_saveCooldown);
        set => SendInt(value, ref _saveCooldown, v => v >= 0, Keys.SaveCooldown, "The save cooldown must be a whole number of seconds, 0 or more.");
    }

    /// <summary>The probed codecs, or the current one alone before the probe.</summary>
    public IReadOnlyList<CodecItem> Codecs
    {
        get => _codecs;
        private set => Set(ref _codecs, value);
    }

    public CodecItem? SelectedCodec
    {
        get => _codecs.FirstOrDefault(c => c.Codec == _video.Codec);
        set
        {
            if (value is null || value.Codec == _video.Codec)
            {
                return;
            }

            SendVideo(_video with { Codec = value.Codec }, nameof(SelectedCodec));
        }
    }

    /// <summary>The offered resolutions, plus the current one if it isn't offered.</summary>
    public IReadOnlyList<ResolutionItem> Resolutions
    {
        get => _resolutions;
        private set => Set(ref _resolutions, value);
    }

    public ResolutionItem? SelectedResolution
    {
        get => _resolutions.FirstOrDefault(r => r.Width == _video.Width && r.Height == _video.Height);
        set
        {
            if (value is null || (value.Width == _video.Width && value.Height == _video.Height))
            {
                return;
            }

            if (!IsDimension(value.Width) || !IsDimension(value.Height))
            {
                Refuse("The resolution must have even sides, each 2 to " + Text(VideoSettings.MaxDimension) + ".", nameof(SelectedResolution));
                return;
            }

            SendVideo(_video with { Width = value.Width, Height = value.Height }, nameof(SelectedResolution));
        }
    }

    /// <summary>The fps ceiling the engine offers.</summary>
    public int MaxFps => _options.MaxFps;

    public string FpsText
    {
        get => Text(_video.Fps);
        set
        {
            if (!TryParse(value, out var fps) || fps < 1 || fps > MaxFps)
            {
                Refuse("The fps must be a whole number from 1 to " + Text(MaxFps) + ".", nameof(FpsText));
                return;
            }

            SendVideo(_video with { Fps = fps }, nameof(FpsText));
        }
    }

    /// <summary>The video bitrate, in kbps.</summary>
    public string BitrateText
    {
        get => Text(_video.BitrateKbps);
        set
        {
            if (!TryParse(value, out var kbps) || kbps < 1)
            {
                Refuse("The bitrate must be a whole number of kbps, 1 or more.", nameof(BitrateText));
                return;
            }

            SendVideo(_video with { BitrateKbps = kbps }, nameof(BitrateText));
        }
    }

    /// <summary>The rate control is VBR rather than CBR.</summary>
    public bool IsVbr
    {
        get => _video.RateControl == RateControl.Vbr;
        set => SendVideo(_video with { RateControl = value ? RateControl.Vbr : RateControl.Cbr }, nameof(IsVbr));
    }

    /// <summary>The estimate of the Global settings, e.g. "Estimated RAM use: 1.6 GB".</summary>
    public string EstimateText => _estimate switch
    {
        null => "Estimate not available yet",
        var e => (e.Storage == ReplayStorage.Memory ? "Estimated RAM use: " : "Estimated disk use: ")
            + FormatBytes(e.Bytes)
            + (e.WorstCase ? " (worst case for VBR)" : ""),
    };

    /// <summary>The warning when the estimate is over the free bytes; null otherwise. It blocks nothing.</summary>
    public string? EstimateWarning => _estimate switch
    {
        { ExceedsFree: true, Storage: ReplayStorage.Memory } e =>
            $"More than the free RAM ({FormatBytes(e.FreeBytes ?? 0)}). The value is kept; Clips may be shorter.",
        { ExceedsFree: true } e =>
            $"More than the free space on the ring folder's drive ({FormatBytes(e.FreeBytes ?? 0)}). The value is kept.",
        _ => null,
    };

    /// <summary>Takes the snapshot's video options and storage estimate. Called before <see cref="SettingsPage.Render"/>.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Equals(_options, state.VideoOptions))
        {
            _options = state.VideoOptions;
            OnPropertyChanged(nameof(MaxFps));
            ShowLists();
        }

        if (!Equals(_estimate, state.Estimate))
        {
            _estimate = state.Estimate;
            OnPropertyChanged(nameof(EstimateText));
            OnPropertyChanged(nameof(EstimateWarning));
        }
    }

    /// <summary>Bytes with one decimal, in GB (10^9) from 1 GB, else in MB (10^6).</summary>
    internal static string FormatBytes(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB");

    protected override FrameworkElement CreateView() => new ReplayBufferSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _bufferWindow = SettingsValues.Global(document, Keys.BufferWindow);
        _storage = SettingsValues.Global(document, Keys.ReplayStorage);
        _ringPath = SettingsValues.Global(document, Keys.RingPath);
        _saveCooldown = SettingsValues.Global(document, Keys.SaveCooldown);
        _video = SettingsValues.Global(document, Keys.Video);
        ShowLists();
        foreach (var name in (string[])[nameof(BufferWindowText), nameof(IsMemory), nameof(IsDisk), nameof(RingPath), nameof(SaveCooldownText)])
        {
            OnPropertyChanged(name);
        }

        ShowVideo();
    }

    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => codec.ToString(),
    };

    private static CodecItem Codec(VideoCodec codec, bool mayNotPlayEverywhere) =>
        new(codec, CodecName(codec) + (mayNotPlayEverywhere ? " (may not play everywhere)" : ""));

    private static ResolutionItem Resolution(int width, int height) =>
        new(width, height, string.Create(CultureInfo.InvariantCulture, $"{width} × {height}"));

    private static bool IsDimension(int value) => value is >= 2 and <= VideoSettings.MaxDimension && value % 2 == 0;

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryParse(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void ShowLists()
    {
        List<CodecItem> codecs = [.. _options.Codecs.Select(c => Codec(c.Codec, c.MayNotPlayEverywhere))];
        if (!codecs.Any(c => c.Codec == _video.Codec))
        {
            codecs.Insert(0, Codec(_video.Codec, VideoOptions.MayNotPlayEverywhereFor(_video.Codec)));
        }

        List<ResolutionItem> resolutions = [.. _options.Resolutions.Select(r => Resolution(r.Width, r.Height))];
        if (!resolutions.Any(r => r.Width == _video.Width && r.Height == _video.Height))
        {
            resolutions.Add(Resolution(_video.Width, _video.Height));
        }

        // The selections follow the lists, so a combo box that cleared its selection gets it back.
        if (!codecs.SequenceEqual(_codecs))
        {
            Codecs = codecs;
        }

        if (!resolutions.SequenceEqual(_resolutions))
        {
            Resolutions = resolutions;
        }

        OnPropertyChanged(nameof(SelectedCodec));
        OnPropertyChanged(nameof(SelectedResolution));
    }

    private void ShowVideo()
    {
        foreach (var name in (string[])[nameof(SelectedCodec), nameof(SelectedResolution), nameof(FpsText), nameof(BitrateText), nameof(IsVbr)])
        {
            OnPropertyChanged(name);
        }
    }

    private void SendInt(string? text, ref int field, Func<int, bool> isValid, SettingKey<int> key, string refusal, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (!TryParse(text, out var value) || !isValid(value))
        {
            Refuse(refusal, property!);
            return;
        }

        if (value == field)
        {
            OnPropertyChanged(property);
            return;
        }

        field = value;
        OnPropertyChanged(property);
        _ = SendAsync([SettingsValues.SetGlobal(key, value)]);
    }

    private void SendStorage(ReplayStorage storage)
    {
        if (storage == _storage)
        {
            return;
        }

        _storage = storage;
        OnPropertyChanged(nameof(IsMemory));
        OnPropertyChanged(nameof(IsDisk));
        _ = SendAsync([SettingsValues.SetGlobal(Keys.ReplayStorage, storage)]);
    }

    // Sends the whole video value with one field changed.
    private void SendVideo(VideoSettings video, string property)
    {
        if (video == _video)
        {
            OnPropertyChanged(property);
            return;
        }

        _video = video;
        ShowVideo();
        _ = SendAsync([SettingsValues.SetGlobal(Keys.Video, video)]);
    }

    // Shows the rendered value again, with why the entry wasn't sent.
    private void Refuse(string message, string property)
    {
        Message = message;
        OnPropertyChanged(property);
    }
}
