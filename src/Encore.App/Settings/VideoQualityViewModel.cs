using System.Globalization;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The video quality editor: codec, resolution, frame rate, bitrate and rate control, sent as one <c>video</c> value.</summary>
internal sealed class VideoQualityViewModel : Observable
{
    /// <summary>"Source" first in <see cref="Resolutions"/>.</summary>
    public const string SourceLabel = "Source — the game's or screen's own size";

    private readonly string? _profileId;
    private readonly Func<IReadOnlyList<SettingsOp>, Task<string?>> _send;
    private readonly Action<string> _showMessage;
    private SettingsDocument _document = SettingsDocument.Empty;
    private VideoSettings _video = Keys.Video.Default;
    private VideoOptions _options = VideoOptions.Unprobed;
    private IReadOnlyList<CodecItem> _codecs = [];
    private IReadOnlyList<ResolutionItem> _resolutions = [];
    private bool _usesGlobal;

    /// <summary>With a profile id it edits that game's override; send and showMessage are the owning page's.</summary>
    public VideoQualityViewModel(string? profileId, Func<IReadOnlyList<SettingsOp>, Task<string?>> send, Action<string> showMessage)
    {
        _profileId = profileId;
        _send = send;
        _showMessage = showMessage;
        _usesGlobal = profileId is not null;
    }

    /// <summary>Whether the game has no <c>video</c> override; ticking removes it, unticking copies the Global value.</summary>
    public bool UsesGlobal
    {
        get => _usesGlobal;
        set
        {
            if (_profileId is null || value == _usesGlobal)
            {
                return;
            }

            ShowUsesGlobal(value);
            _video = SettingsValues.Global(_document, Keys.Video);
            ShowLists();
            ShowVideo();
            _ = _send([SettingsValues.UseGlobal(Keys.Video, _profileId, value, _video)]);
        }
    }

    public bool CanEdit => _profileId is null || !UsesGlobal;

    /// <summary>The probed codecs, or the current one alone before the probe.</summary>
    public IReadOnlyList<CodecItem> Codecs
    {
        get => _codecs;
        private set => Set(ref _codecs, value);
    }

    public CodecItem? SelectedCodec
    {
        get => _codecs.FirstOrDefault(item => item.Codec == _video.Codec);
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

    /// <summary>The selected resolution; choosing Source turns <c>matchSource</c> on and keeps the size as the fallback.</summary>
    public ResolutionItem? SelectedResolution
    {
        get => _video.MatchSource
            ? _resolutions.FirstOrDefault(item => item.IsSource)
            : _resolutions.FirstOrDefault(item => IsCurrentSize(item));
        set
        {
            if (value is not null)
            {
                SelectResolution(value);
            }
        }
    }

    /// <summary>What "Source" records at, shown only while it's chosen; the H.264 line is there for its 4096 px limit.</summary>
    public string? SourceHelp => _video.MatchSource
        ? "Clips and Recordings are recorded at the game window's own size, and Monitor Recordings at the screen's size."
            + " Frame rate and bitrate still apply."
            + (_video.Codec == VideoCodec.H264 ? " With H.264, anything wider or taller than 4096 px is scaled down to fit." : "")
        : null;

    /// <summary>The fps ceiling the engine offers.</summary>
    public int MaxFps => _options.MaxFps;

    public string FpsText
    {
        get => SettingsText.Whole(_video.Fps);
        set
        {
            if (!SettingsText.TryParseWhole(value, out var fps) || fps < 1 || fps > MaxFps)
            {
                Refuse("The fps must be a whole number from 1 to " + SettingsText.Whole(MaxFps) + ".", nameof(FpsText));
                return;
            }

            SendVideo(_video with { Fps = fps }, nameof(FpsText));
        }
    }

    /// <summary>The video bitrate, in kbps.</summary>
    public string BitrateText
    {
        get => SettingsText.Whole(_video.BitrateKbps);
        set
        {
            if (!SettingsText.TryParseWhole(value, out var kilobitsPerSecond) || kilobitsPerSecond < 1)
            {
                Refuse("The bitrate must be a whole number of kbps, 1 or more.", nameof(BitrateText));
                return;
            }

            SendVideo(_video with { BitrateKbps = kilobitsPerSecond }, nameof(BitrateText));
        }
    }

    /// <summary>The rate control is VBR rather than CBR.</summary>
    public bool IsVbr
    {
        get => _video.RateControl == RateControl.Vbr;
        set => SendVideo(_video with { RateControl = value ? RateControl.Vbr : RateControl.Cbr }, nameof(IsVbr));
    }

    /// <summary>Takes the codecs, resolutions and fps ceiling the engine's probe offers.</summary>
    public void ShowOptions(VideoOptions options)
    {
        if (Equals(_options, options))
        {
            return;
        }

        _options = options;
        OnPropertyChanged(nameof(MaxFps));
        ShowLists();
    }

    /// <summary>Shows the document's <c>video</c> value: the game's own, or the Global one it uses.</summary>
    public void Render(SettingsDocument document)
    {
        _document = document;
        if (_profileId is not null)
        {
            ShowUsesGlobal(!SettingsValues.HasOverride(document, Keys.Video, _profileId));
        }

        _video = SettingsValues.Resolve(document, Keys.Video, _profileId);
        ShowLists();
        ShowVideo();
    }

    private void ShowVideo()
    {
        foreach (var name in (string[])[nameof(SelectedCodec), nameof(SelectedResolution), nameof(SourceHelp), nameof(FpsText),
            nameof(BitrateText), nameof(IsVbr)])
        {
            OnPropertyChanged(name);
        }
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

    private static ResolutionItem Resolution(int width, int height) =>
        new(width, height, string.Create(CultureInfo.InvariantCulture, $"{width} × {height}"));

    private static bool IsDimension(int value) => value is >= 2 and <= VideoSettings.MaxDimension && value % 2 == 0;

    private bool IsCurrentSize(ResolutionItem item) => !item.IsSource && item.Width == _video.Width && item.Height == _video.Height;

    private void SelectResolution(ResolutionItem item)
    {
        if (item.IsSource)
        {
            SendVideo(_video with { MatchSource = true }, nameof(SelectedResolution));
            return;
        }

        if (!_video.MatchSource && item.Width == _video.Width && item.Height == _video.Height)
        {
            return;
        }

        if (!IsDimension(item.Width) || !IsDimension(item.Height))
        {
            var limit = SettingsText.Whole(VideoSettings.MaxDimension);
            Refuse("The resolution must have even sides, each 2 to " + limit + ".", nameof(SelectedResolution));
            return;
        }

        SendVideo(_video with { Width = item.Width, Height = item.Height, MatchSource = false }, nameof(SelectedResolution));
    }

    private void ShowUsesGlobal(bool value)
    {
        if (Set(ref _usesGlobal, value, nameof(UsesGlobal)))
        {
            OnPropertyChanged(nameof(CanEdit));
        }
    }

    private void ShowLists()
    {
        var codecs = OfferedCodecs();
        var resolutions = OfferedResolutions();

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

    private List<CodecItem> OfferedCodecs()
    {
        List<CodecItem> codecs = [.. _options.Codecs.Select(option => Codec(option.Codec, option.MayNotPlayEverywhere))];
        if (!codecs.Any(item => item.Codec == _video.Codec))
        {
            codecs.Insert(0, Codec(_video.Codec, VideoOptions.MayNotPlayEverywhereFor(_video.Codec)));
        }

        return codecs;
    }

    private List<ResolutionItem> OfferedResolutions()
    {
        List<ResolutionItem> resolutions =
        [
            new(0, 0, SourceLabel, IsSource: true),
            .. _options.Resolutions.Select(option => Resolution(option.Width, option.Height)),
        ];
        if (!_video.MatchSource && !resolutions.Any(IsCurrentSize))
        {
            resolutions.Add(Resolution(_video.Width, _video.Height));
        }

        return resolutions;
    }

    // Sends the whole video value with one field changed.
    private void SendVideo(VideoSettings video, string property)
    {
        if (video == _video || !CanEdit)
        {
            OnPropertyChanged(property);
            return;
        }

        _video = video;
        ShowVideo();
        _ = _send([SettingsValues.Set(Keys.Video, video, _profileId)]);
    }

    // Shows the rendered value again, with why the entry wasn't sent.
    private void Refuse(string message, string property)
    {
        _showMessage(message);
        OnPropertyChanged(property);
    }
}
