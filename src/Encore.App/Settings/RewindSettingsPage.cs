using System.Globalization;
using System.IO;
using System.Windows;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A codec the page offers, with its label.</summary>
internal sealed record CodecItem(VideoCodec Codec, string Label);

/// <summary>An output resolution the page offers; IsSource is "Source", the game's or screen's own size.</summary>
internal sealed record ResolutionItem(int Width, int Height, string Label, bool IsSource = false);

/// <summary>The Clips and Output settings, each edit sent at once; with a profile id it edits that game's overrides.</summary>
internal sealed class RewindSettingsPage(SettingsSender sender, string? profileId = null, string? defaultCacheFolder = null) : SettingsPage(sender)
{
    public const string CacheInsideClips = "The cache folder can't be inside the Clips folder.";
    public const string CantWrite = "Encore can't write to that folder.";
    public const string ClipsContainsCache = "The Clips folder can't contain the cache folder.";

    private int _bufferWindow = Keys.BufferWindow.Default;
    private ReplayStorage _storage = Keys.ReplayStorage.Default;
    private string? _ringPath = Keys.RingPath.Default;
    private int _saveCooldown = Keys.SaveCooldown.Default;
    private VideoSettings _video = Keys.Video.Default;
    private VideoOptions _options = VideoOptions.Unprobed;
    private StorageEstimate? _estimate;
    private IReadOnlyList<CodecItem> _codecs = [];
    private IReadOnlyList<ResolutionItem> _resolutions = [];
    private bool _bufferWindowUsesGlobal = profileId is not null;
    private bool _videoUsesGlobal = profileId is not null;
    private FolderPicker? _cacheFolder;
    private string? _clipsFolder;
    private string? _libraryFolder;
    private HotkeyBinding? _saveKey;

    public override string Title => "Clips";

    /// <summary>The page's intro: what it controls, with the Save Hotkey.</summary>
    public string Intro =>
        $"Encore keeps the last part of your game ready, so {(_saveKey is { } key ? HotkeysPage.Describe(key) : "the Save Clip key")} can save it as a Clip.";

    /// <summary>The cache folder (<c>ringPath</c>): enabled only under Disk.</summary>
    public FolderPicker CacheFolder => _cacheFolder ??= new FolderPicker(Keys.RingPath, ops => SendForPickerAsync(ops, ErrorCodes.RingPathInvalid))
    {
        RefusalText = code => code == ErrorCodes.RingPathInvalid ? CacheInsideClips : null,
        DialogTitle = "Choose the cache folder",
        DisabledCaption = "Used only in Disk mode",
        Validate = ValidateCacheFolder,
    };

    /// <summary>The cache folder's help: what it holds, with the Clip length.</summary>
    public string CacheHelp => $"In Disk mode, the last {HotkeysPage.ClipLength(_bufferWindow)} of gameplay is kept here in one file. It doesn't grow.";

    /// <summary>The Memory option's line, with the estimate.</summary>
    public string MemoryLine => _estimate is { } e ? $"Fastest. Uses about {FormatBytes(e.Bytes)} of RAM." : "Fastest. Uses RAM.";

    /// <summary>The Disk option's line, with the estimate.</summary>
    public string DiskLine => _estimate is { } e ? $"Uses about {FormatBytes(e.Bytes)} in the cache folder instead of RAM." : "Uses the cache folder instead of RAM.";

    /// <summary>The Game Profile whose overrides this page edits; null on the Global page.</summary>
    public string? ProfileId { get; } = profileId;

    /// <summary>The Global page: it also shows storage, the ring folder, the save cooldown and the estimate.</summary>
    public bool IsGlobal => ProfileId is null;

    public bool IsProfile => !IsGlobal;

    /// <summary>Whether the profile has no <c>bufferWindow</c> override; ticking removes it, unticking copies the Global value.</summary>
    public bool BufferWindowUsesGlobal
    {
        get => _bufferWindowUsesGlobal;
        set
        {
            if (IsGlobal || value == _bufferWindowUsesGlobal)
            {
                return;
            }

            ShowBufferWindowUsesGlobal(value);
            _bufferWindow = SettingsValues.Global(Document, Keys.BufferWindow);
            OnPropertyChanged(nameof(BufferWindowText));
            _ = SendAsync([UseGlobalOp(Keys.BufferWindow, value, _bufferWindow)]);
        }
    }

    public bool CanEditBufferWindow => IsGlobal || !BufferWindowUsesGlobal;

    /// <summary>Whether the profile has no <c>video</c> override; as <see cref="BufferWindowUsesGlobal"/>.</summary>
    public bool VideoUsesGlobal
    {
        get => _videoUsesGlobal;
        set
        {
            if (IsGlobal || value == _videoUsesGlobal)
            {
                return;
            }

            ShowVideoUsesGlobal(value);
            _video = SettingsValues.Global(Document, Keys.Video);
            ShowLists();
            ShowVideo();
            _ = SendAsync([UseGlobalOp(Keys.Video, value, _video)]);
        }
    }

    public bool CanEditVideo => IsGlobal || !VideoUsesGlobal;

    /// <summary><c>bufferWindow</c>, in s; at least 1.</summary>
    public string BufferWindowText
    {
        get => Text(_bufferWindow);
        set
        {
            if (!CanEditBufferWindow)
            {
                OnPropertyChanged();
                return;
            }

            SendInt(value, ref _bufferWindow, v => v >= 1, Keys.BufferWindow, ProfileId, "The Clip length must be a whole number of seconds, 1 or more.");
        }
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

    /// <summary><c>saveCooldown</c>, in s; 0 or more.</summary>
    public string SaveCooldownText
    {
        get => Text(_saveCooldown);
        set => SendInt(value, ref _saveCooldown, v => v >= 0, Keys.SaveCooldown, null, "Time between saves must be a whole number of seconds, 0 or more.");
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

    /// <summary>"Source" first in <see cref="Resolutions"/>.</summary>
    public const string SourceLabel = "Source — the game's or screen's own size";

    /// <summary>The selected resolution; choosing Source turns <c>matchSource</c> on and keeps the size as the fallback.</summary>
    public ResolutionItem? SelectedResolution
    {
        get => _video.MatchSource
            ? _resolutions.FirstOrDefault(r => r.IsSource)
            : _resolutions.FirstOrDefault(r => !r.IsSource && r.Width == _video.Width && r.Height == _video.Height);
        set
        {
            if (value is null)
            {
                return;
            }

            if (value.IsSource)
            {
                SendVideo(_video with { MatchSource = true }, nameof(SelectedResolution));
                return;
            }

            if (!_video.MatchSource && value.Width == _video.Width && value.Height == _video.Height)
            {
                return;
            }

            if (!IsDimension(value.Width) || !IsDimension(value.Height))
            {
                Refuse("The resolution must have even sides, each 2 to " + Text(VideoSettings.MaxDimension) + ".", nameof(SelectedResolution));
                return;
            }

            SendVideo(_video with { Width = value.Width, Height = value.Height, MatchSource = false }, nameof(SelectedResolution));
        }
    }

    /// <summary>What "Source" records at, shown only while it's chosen; the H.264 line is there for its 4096 px limit.</summary>
    public string? SourceHelp => _video.MatchSource
        ? "Clips and Recordings are recorded at the game window's own size, and Monitor Recordings at the screen's size. Frame rate and bitrate still apply."
            + (_video.Codec == VideoCodec.H264 ? " With H.264, anything wider or taller than 4096 px is scaled down to fit." : "")
        : null;

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
            $"More than the free space on the cache folder's drive ({FormatBytes(e.FreeBytes ?? 0)}). The value is kept.",
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
            OnPropertyChanged(nameof(MemoryLine));
            OnPropertyChanged(nameof(DiskLine));
        }

        _libraryFolder = state.LibraryFolder;
    }

    /// <summary>Bytes with one decimal, in GB (10^9) from 1 GB, else in MB (10^6).</summary>
    internal static string FormatBytes(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB");

    // A game's Clip length and Video quality show in their own editors on its page, so only the main page has a view.
    protected override FrameworkElement CreateView() => new RewindSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        if (IsProfile)
        {
            ShowBufferWindowUsesGlobal(!SettingsValues.HasOverride(document, Keys.BufferWindow, ProfileId!));
            ShowVideoUsesGlobal(!SettingsValues.HasOverride(document, Keys.Video, ProfileId!));
        }

        _bufferWindow = SettingsValues.Resolve(document, Keys.BufferWindow, ProfileId);
        _storage = SettingsValues.Global(document, Keys.ReplayStorage);
        _ringPath = SettingsValues.Global(document, Keys.RingPath);
        _clipsFolder = SettingsValues.Global(document, Keys.OutputFolder);
        _saveKey = SettingsValues.Global(document, Keys.Hotkeys).Save;
        if (IsGlobal)
        {
            CacheFolder.Show(_ringPath, defaultCacheFolder ?? "");
            CacheFolder.IsEnabled = _storage == ReplayStorage.Disk;
        }
        _saveCooldown = SettingsValues.Global(document, Keys.SaveCooldown);
        _video = SettingsValues.Resolve(document, Keys.Video, ProfileId);
        ShowLists();
        foreach (var name in (string[])[nameof(BufferWindowText), nameof(IsMemory), nameof(IsDisk), nameof(SaveCooldownText), nameof(Intro), nameof(CacheHelp)])
        {
            OnPropertyChanged(name);
        }

        ShowVideo();
    }

    // The glossary's codec names; each says how widely it plays.
    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264 (plays everywhere)",
        VideoCodec.Hevc => "HEVC (smaller files)",
        VideoCodec.Av1 => "AV1 (smallest; newer players only)",
        _ => codec.ToString(),
    };

    private static CodecItem Codec(VideoCodec codec, bool mayNotPlayEverywhere) =>
        new(codec, CodecName(codec) + (mayNotPlayEverywhere && codec == VideoCodec.Hevc ? " — may not play everywhere" : ""));

    /// <summary>The cache folder's refusals: inside the Clips folder, or not writable.</summary>
    private string? ValidateCacheFolder(string path)
    {
        if ((_clipsFolder ?? _libraryFolder) is { Length: > 0 } clips && IsInside(path, clips))
        {
            return CacheInsideClips;
        }

        return FolderProbe.CanWrite(path) ? null : CantWrite;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it, ignoring case.</summary>
    internal static bool IsInside(string path, string folder)
    {
        // A drive root keeps its separator, so add one only where it's missing.
        static string WithSeparator(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
        return WithSeparator(Path.GetFullPath(path)).StartsWith(WithSeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
    }

    private static ResolutionItem Resolution(int width, int height) =>
        new(width, height, string.Create(CultureInfo.InvariantCulture, $"{width} × {height}"));

    private static bool IsDimension(int value) => value is >= 2 and <= VideoSettings.MaxDimension && value % 2 == 0;

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryParse(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private SettingsOp UseGlobalOp<T>(SettingKey<T> key, bool useGlobal, T global) => useGlobal
        ? SettingsOp.RemoveProfile(ProfileId!, key.Name)
        : SettingsValues.Set(key, global, ProfileId);

    private void ShowBufferWindowUsesGlobal(bool value)
    {
        if (Set(ref _bufferWindowUsesGlobal, value, nameof(BufferWindowUsesGlobal)))
        {
            OnPropertyChanged(nameof(CanEditBufferWindow));
        }
    }

    private void ShowVideoUsesGlobal(bool value)
    {
        if (Set(ref _videoUsesGlobal, value, nameof(VideoUsesGlobal)))
        {
            OnPropertyChanged(nameof(CanEditVideo));
        }
    }

    private void ShowLists()
    {
        List<CodecItem> codecs = [.. _options.Codecs.Select(c => Codec(c.Codec, c.MayNotPlayEverywhere))];
        if (!codecs.Any(c => c.Codec == _video.Codec))
        {
            codecs.Insert(0, Codec(_video.Codec, VideoOptions.MayNotPlayEverywhereFor(_video.Codec)));
        }

        List<ResolutionItem> resolutions = [new(0, 0, SourceLabel, IsSource: true), .. _options.Resolutions.Select(r => Resolution(r.Width, r.Height))];
        if (!_video.MatchSource && !resolutions.Any(r => !r.IsSource && r.Width == _video.Width && r.Height == _video.Height))
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
        foreach (var name in (string[])[nameof(SelectedCodec), nameof(SelectedResolution), nameof(SourceHelp), nameof(FpsText), nameof(BitrateText), nameof(IsVbr)])
        {
            OnPropertyChanged(name);
        }
    }

    private void SendInt(string? text, ref int field, Func<int, bool> isValid, SettingKey<int> key, string? scope, string refusal, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
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
        _ = SendAsync([SettingsValues.Set(key, value, scope)]);
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
        CacheFolder.IsEnabled = storage == ReplayStorage.Disk;
        _ = SendAsync([SettingsValues.SetGlobal(Keys.ReplayStorage, storage)]);
    }

    // Sends the whole video value with one field changed.
    private void SendVideo(VideoSettings video, string property)
    {
        if (video == _video || !CanEditVideo)
        {
            OnPropertyChanged(property);
            return;
        }

        _video = video;
        ShowVideo();
        _ = SendAsync([SettingsValues.Set(Keys.Video, video, ProfileId)]);
    }

    // Shows the rendered value again, with why the entry wasn't sent.
    private void Refuse(string message, string property)
    {
        Message = message;
        OnPropertyChanged(property);
    }
}
