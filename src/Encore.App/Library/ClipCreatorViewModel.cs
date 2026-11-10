using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The clip creator: one selection of a source file, previewed and exported. UI thread.</summary>
internal sealed class ClipCreatorViewModel : Observable, IDisposable
{
    private readonly IClipMediaService _media;
    private readonly Func<IPlayer> _newPlayer;
    private readonly Func<string> _createPreviewFolder;
    private readonly ClipThumbnailStore? _thumbnailStore;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _preparationCancellation;
    private CancellationTokenSource? _viewportCancellation;
    private Task? _preparation;
    private Task? _thumbnailPreparation;
    private CancellationTokenSource? _thumbnailCancellation;
    private ClipThumbnailImage[] _thumbnails = [];
    private bool _thumbnailFailed;
    private ClipWaveformCache? _cache;
    private int _viewportRevision;
    private bool _scrubbing;
    private bool _trimming;
    private bool _suspended;
    private string? _rangeFeedback;
    private PlayerViewModel? _sourcePlayer;
    private PlayerViewModel? _preview;
    private string? _previewFolder;
    private bool _renderedPreview;
    private ClipMetadata? _metadata;
    private ClipRange? _effective;
    private ClipWaveform[] _waveforms = [];
    private double _start, _end, _viewportStart, _viewportEnd, _playhead, _progress;
    private bool _mix, _busy, _exporting, _closed;
    private string _exportName;
    private bool _choosingDestination;
    private string _format = "mp4", _status = "";
    private int _revision;
    private int _sourceCheck;
    private double _openingPosition;

    /// <summary>createPreviewFolder makes the scratch folder of a rendered preview; tests replace it.</summary>
    public ClipCreatorViewModel(
        ClipSource source,
        string title,
        IClipMediaService media,
        Func<IPlayer> newPlayer,
        Func<string>? createPreviewFolder = null,
        string? thumbnailCacheFolder = null)
    {
        Source = source;
        Title = title;
        _exportName = Path.GetFileNameWithoutExtension(source.Path) + " clip";
        _media = media;
        _newPlayer = newPlayer;
        _thumbnailStore = thumbnailCacheFolder is null ? null : new ClipThumbnailStore(thumbnailCacheFolder);
        _createPreviewFolder = createPreviewFolder ?? CreateTempPreviewFolder;
        CancelCommand = new RelayCommand(() => _operation?.Cancel());
        PreviewCommand = new RelayCommand(() => _ = PreviewAsync(), () => CanExport);
        ZoomInCommand = new RelayCommand(() => Zoom(0.5), () => CanEdit && ViewportEnd - ViewportStart > Math.Min(0.01, Duration));
        ZoomOutCommand = new RelayCommand(() => Zoom(2), () => CanEdit && ViewportEnd - ViewportStart < Duration);
        PanLeftCommand = new RelayCommand(() => Pan(-0.5), () => CanEdit && ViewportStart > 0);
        PanRightCommand = new RelayCommand(() => Pan(0.5), () => CanEdit && ViewportEnd < Duration);
        SkipBackCommand = new RelayCommand(() => Seek(Playhead - 10), () => CanEdit);
        SkipForwardCommand = new RelayCommand(() => Seek(Playhead + 10), () => CanEdit);
        AllTracksCommand = new RelayCommand(() => IncludeAllTracks(true));
        NoTracksCommand = new RelayCommand(() => IncludeAllTracks(false));
    }

    public ClipSource Source { get; }

    public string Title { get; }

    public string ExportName
    {
        get => _exportName;
        set
        {
            if (Set(ref _exportName, value))
            {
                NotifyCommands();
            }
        }
    }

    public string ExportLabel => IsMp3 ? "Export audio" : "Export video";

    public string SaveLabel => IsMp3 ? "Save audio…" : "Save video…";

    public string FormatSummary => IsMp3 ? "MP3 · Stereo · 48 kHz / 192 kbps" : "MP4 · H.264 video / AAC audio · Source resolution";

    public string SeparateLabel => IsMp3 ? "Separate MP3 files" : "Separate audio tracks";

    public bool MultipleTracks => Tracks.Count(track => track.Included) > 1;

    public bool SeparateAudioFiles => IsMp3 && !Mix;

    /// <summary>What the export will hold, for the checked tracks, the format and the mix choice.</summary>
    public string OutputSummary
    {
        get
        {
            if (!Tracks.Any(track => track.Included))
            {
                return IsMp3 ? "Choose at least one audio track." : "Silent video with no audio.";
            }

            if (!MultipleTracks)
            {
                return IsMp3 && !Mix
                    ? "The selected track publishes as a named MP3 in one new folder."
                    : "Only the selected track will be included.";
            }

            if (Mix)
            {
                return MixWarning;
            }

            return IsMp3
                ? "Named MP3 files publish together in one new folder."
                : "Named audio streams stay selectable in one MP4, in source order.";
        }
    }

    public bool ValidExportName => !string.IsNullOrWhiteSpace(ExportName) && ExportName is not "." and not ".."
        && ExportName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && ExportName == ExportName.Trim().TrimEnd('.');

    public bool CanSave => CanExport && ValidExportName && !_choosingDestination;

    public bool CanOpenOptions => CanEdit && !_choosingDestination;

    /// <summary>The chooser is separate from encoding and injectable, so cancellation cannot start work.</summary>
    internal async Task ChooseAndExportAsync(Func<ClipCreatorViewModel, Task<string?>> choose)
    {
        if (!CanSave)
        {
            return;
        }

        _choosingDestination = true;
        NotifyCommands();
        try
        {
            var destination = await choose(this);
            if (destination is not null && !_closed)
            {
                await ExportAsync(destination);
            }
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
        finally
        {
            _choosingDestination = false;
            NotifyCommands();
        }
    }

    public Action? SourceInvalidated { get; set; }

    public ICommand? BackCommand { get; set; }

    public string PreviewLabel => _renderedPreview ? "Rendered selection preview" : "Source video";

    public ObservableCollection<ClipCreatorTrack> Tracks { get; } = [];

    public ICommand CancelCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand PanLeftCommand { get; }

    public ICommand PanRightCommand { get; }

    public ICommand SkipBackCommand { get; }

    public ICommand SkipForwardCommand { get; }

    public ICommand AllTracksCommand { get; }

    public ICommand NoTracksCommand { get; }

    public bool IsClosed => _closed;

    public ClipMetadata? Metadata
    {
        get => _metadata;
        private set
        {
            Set(ref _metadata, value);
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(PlaybackText));
        }
    }

    public double Duration => Metadata?.Duration ?? 0;

    public double Start
    {
        get => _start;
        set => SetRange(value, End);
    }

    public double End
    {
        get => _end;
        set => SetRange(Start, value);
    }

    public string StartText
    {
        get => FormatTime(Start);
        set
        {
            if (TryTime(value, out var time))
            {
                Start = time;
            }
            else
            {
                RangeError("Enter start as HH:MM:SS.mmm.");
            }

            OnPropertyChanged();
        }
    }

    public string EndText
    {
        get => FormatTime(End);
        set
        {
            if (TryTime(value, out var time))
            {
                End = time;
            }
            else
            {
                RangeError("Enter end as HH:MM:SS.mmm.");
            }

            OnPropertyChanged();
        }
    }

    public string SelectedDurationText => FormatTime(TimeSpan.FromSeconds(Math.Max(0, End - Start)));

    public string SelectionLengthText => Math.Max(0, End - Start).ToString("0.000", CultureInfo.InvariantCulture) + " s";

    public string DurationText => $"Selection {FormatTime(TimeSpan.FromSeconds(Math.Max(0, End - Start)))}";

    /// <summary>The range the export will really cut at, once the worker has resolved it to frames or samples.</summary>
    public string EffectiveText
    {
        get
        {
            if (_effective is { } range)
            {
                var unit = range.Alignment == "audioSamples48000" ? "Effective audio samples (48 kHz)" : "Effective source frames";
                return $"{unit}: {FormatTime(range.Start)} – {FormatTime(range.End)}";
            }

            if (!CanEdit)
            {
                return "";
            }

            return _trimming ? "Release trim handle to resolve effective media boundaries." : "Resolving effective media boundaries…";
        }
    }

    public string PlayheadText => FormatTime(Playhead);

    public string PlaybackText => $"{FormatTime(Playhead)} / {FormatTime(Duration)}";

    public string ViewportText => $"Visible {FormatTime(ViewportStart)} – {FormatTime(ViewportEnd)}";

    public string ZoomText => ViewportEnd > ViewportStart ? $"{Duration / (ViewportEnd - ViewportStart):0.#}×" : "1×";

    public double Playhead
    {
        get => _playhead;
        private set
        {
            if (Set(ref _playhead, value))
            {
                OnPropertyChanged(nameof(PlayheadText));
                OnPropertyChanged(nameof(PlaybackText));
            }
        }
    }

    public double ViewportStart => _viewportStart;

    public double ViewportEnd => _viewportEnd;

    public ClipWaveform[] Waveforms
    {
        get => _waveforms;
        private set => Set(ref _waveforms, value);
    }

    public ClipThumbnailImage[] Thumbnails
    {
        get => _thumbnails;
        private set => Set(ref _thumbnails, value);
    }

    public string ThumbnailHint =>
        "Drag the selected area to move the clip; drag its edge grips to trim. Click to seek, or use the ruler to scrub."
        + (_thumbnailFailed
            ? " Video thumbnails are unavailable. You can still edit and export; reopen the creator to retry thumbnails."
            : "");

    public bool Mix
    {
        get => _mix;
        set
        {
            if (Set(ref _mix, value))
            {
                Changed();
            }
        }
    }

    public bool Separate
    {
        get => !Mix;
        set
        {
            if (value)
            {
                Mix = false;
            }
        }
    }

    public string Format
    {
        get => _format;
        set
        {
            if (value is "mp4" or "mp3" && Set(ref _format, value))
            {
                Changed();
            }
        }
    }

    public bool IsMp4
    {
        get => Format == "mp4";
        set
        {
            if (value)
            {
                Format = "mp4";
            }
        }
    }

    public bool IsMp3
    {
        get => Format == "mp3";
        set
        {
            if (value)
            {
                Format = "mp3";
            }
        }
    }

    public string MixWarning => Mix && Tracks.Count(track => track.Included) > 1
        ? "Mix includes every checked track. Overlapping tracks (such as Everything plus microphone) may count the same sound twice."
            + " Normalization and limiting are applied."
        : "Separate tracks retain their names and order. Preview's audio menu can audition each exported stream.";

    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            NotifyCommands();
        }
    }

    public bool Exporting
    {
        get => _exporting;
        private set
        {
            Set(ref _exporting, value);
            NotifyCommands();
        }
    }

    public bool CanExport => !_closed && !_trimming && Metadata is not null && _effective is not null && !Busy && End > Start
        && (Format != "mp3" || Tracks.Any(track => track.Included));

    public bool CanEdit => !_closed && !Exporting && Metadata is not null;

    public string Status
    {
        get => _rangeFeedback ?? _status;
        private set
        {
            _rangeFeedback = null;
            _status = value;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public PlayerViewModel? Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    public async Task OpenAsync(double position)
    {
        _openingPosition = position;
        await WorkAsync(async token =>
        {
            var result = await _media.ExecuteAsync(new("probe", Source), null, token);
            token.ThrowIfCancellationRequested();
            Metadata = result.Metadata ?? throw new IOException("No media timeline was returned.");
            foreach (var track in Metadata.Tracks)
            {
                var choice = new ClipCreatorTrack(track);
                choice.Changed += Changed;
                Tracks.Add(choice);
            }

            _start = Math.Max(0, Math.Min(Duration - Math.Min(30, Duration), position - 15));
            _end = Math.Min(Duration, _start + 30);
            _viewportStart = _start;
            _viewportEnd = _end;
            NotifyRange();
            OnPropertyChanged(nameof(ViewportStart));
            OnPropertyChanged(nameof(ViewportEnd));
            OnPropertyChanged(nameof(ViewportText));
            OnPropertyChanged(nameof(ZoomText));
            _sourcePlayer = new PlayerViewModel(Source.Path, Title, _newPlayer(), embedded: true);
            ConfigurePlaybackLoop(_sourcePlayer, rendered: false);
            Preview = _sourcePlayer;
            await _sourcePlayer.OpenAsync();
            token.ThrowIfCancellationRequested();
            Seek(Math.Clamp(position, 0, Duration));
        });
        if (!_closed && Metadata is not null)
        {
            await Task.WhenAll(EnsureWaveformsAsync(), RefreshAsync());
            _ = EnsureThumbnailsAsync();
        }
    }

    public void SetRange(double start, double end)
    {
        if (Exporting || _closed || Metadata is null)
        {
            return;
        }

        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > Duration || end <= start)
        {
            RangeError("Choose a non-empty range inside the source.");
            NotifyRange();
            return;
        }

        if (_rangeFeedback is not null)
        {
            _rangeFeedback = null;
            OnPropertyChanged(nameof(Status));
        }

        if (Math.Abs(start - _start) < 0.00000001 && Math.Abs(end - _end) < 0.00000001)
        {
            return;
        }

        (_start, _end) = (start, end);
        NotifyRange();
        if (!_trimming)
        {
            SelectionChanged();
        }
    }

    /// <summary>Moves the start, or the end, of the selection by some milliseconds.</summary>
    public void Nudge(bool end, int milliseconds) =>
        SetRange(Start + (end ? 0 : milliseconds / 1000d), End + (end ? milliseconds / 1000d : 0));

    internal void MoveSelection(double originalStart, double originalEnd, double delta)
    {
        if (!CanEdit || _suspended || !double.IsFinite(delta) || !double.IsFinite(originalStart) || !double.IsFinite(originalEnd)
            || originalStart < 0 || originalEnd > Duration || originalEnd <= originalStart)
        {
            return;
        }

        // Anchor every update to the press range; do not accumulate rounding error or clamp to the viewport.
        var shift = Math.Clamp(Math.Round(delta, 3, MidpointRounding.AwayFromZero), -originalStart, Duration - originalEnd);
        SetRange(Math.Max(0, originalStart + shift), Math.Min(Duration, originalEnd + shift));
    }

    internal void SelectVisibleRange()
    {
        if (!CanEdit || _suspended)
        {
            return;
        }

        // Decimal time avoids losing a millisecond to binary noise at an existing boundary.
        var start = (double)(decimal.Ceiling((decimal)Math.Max(0, ViewportStart) * 1000) / 1000);
        var end = (double)(decimal.Floor((decimal)Math.Min(Duration, ViewportEnd) * 1000) / 1000);
        if (end > start)
        {
            SetRange(start, end);
        }
    }

    internal void BeginRangeDrag()
    {
        if (!CanEdit || _trimming)
        {
            return;
        }

        _trimming = true;
        SelectionChanged();
    }

    internal void EndRangeDrag()
    {
        if (!_trimming)
        {
            return;
        }

        _trimming = false;
        if (!_closed && !_suspended)
        {
            _ = RefreshAsync();
        }
    }

    private void RangeError(string message)
    {
        _rangeFeedback = message;
        OnPropertyChanged(nameof(Status));
    }

    private void ConfigurePlaybackLoop(PlayerViewModel player, bool rendered)
    {
        player.PlaybackLoop = () =>
        {
            if (_closed || _suspended || _scrubbing)
            {
                return null;
            }

            var start = rendered ? 0 : Start;
            var end = rendered ? _effective is { } range ? range.End - range.Start : 0 : End;
            return end > start ? (TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)) : null;
        };
    }

    private void SelectionChanged()
    {
        var previous = Preview;
        var playing = Preview?.IsPlaying == true;
        var position = Playhead;
        Changed();
        if (!ReferenceEquals(previous, Preview))
        {
            Seek(position);
        }

        // A live trim invalidates rendered media. Continue the loop on the source, with the new range.
        if (playing && !_closed && !_suspended && Preview is { IsPlaying: false } player)
        {
            player.PlayPauseCommand.Execute(null);
        }
    }

    public void Zoom(double factor)
    {
        if (!CanEdit)
        {
            return;
        }

        var width = Math.Clamp((_viewportEnd - _viewportStart) * factor, Math.Min(0.01, Duration), Duration);
        SetViewport(Math.Clamp(Playhead, _viewportStart, _viewportEnd) - width / 2, width);
    }

    public void Pan(double fraction)
    {
        if (CanEdit)
        {
            SetViewport(_viewportStart + (_viewportEnd - _viewportStart) * fraction, _viewportEnd - _viewportStart);
        }
    }

    private void SetViewport(double start, double width)
    {
        _viewportStart = Math.Clamp(start, 0, Math.Max(0, Duration - width));
        _viewportEnd = Math.Min(Duration, _viewportStart + width);
        OnPropertyChanged(nameof(ViewportStart));
        OnPropertyChanged(nameof(ViewportEnd));
        OnPropertyChanged(nameof(ViewportText));
        OnPropertyChanged(nameof(ZoomText));
        NotifyCommands();
        _ = ReadWaveformsAsync();
    }

    public void Seek(double position)
    {
        Playhead = Math.Clamp(position, 0, Duration);
        if (_renderedPreview && _effective is { } range && (Playhead < range.Start || Playhead >= range.End))
        {
            InvalidatePreview();
        }

        var previewPosition = _renderedPreview && _effective is { } rendered
            ? Math.Clamp(Playhead - rendered.Start, 0, rendered.End - rendered.Start)
            : Playhead;
        Preview?.SeekTo(TimeSpan.FromSeconds(previewPosition));
    }

    internal bool IsScrubbing => _scrubbing;

    internal void BeginScrub(double position)
    {
        _scrubbing = true;
        Seek(position);
    }

    internal void EndScrub()
    {
        _scrubbing = false;
    }

    public void Tick()
    {
        if (_closed)
        {
            return;
        }

        if (++_sourceCheck % 10 == 0)
        {
            var valid = false;
            try
            {
                var file = new FileInfo(Source.Path);
                valid = file.Exists && file.Length == Source.Length && file.LastWriteTimeUtc.Ticks == Source.ModifiedUtcTicks;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            if (!valid)
            {
                Dispose();
                SourceInvalidated?.Invoke();
                return;
            }
        }

        if (!_scrubbing && Preview is { IsClosed: false } player)
        {
            player.RefreshPosition();
            Playhead = Math.Clamp(player.Position.TotalSeconds + (_renderedPreview ? _effective?.Start ?? 0 : 0), 0, Duration);
        }
    }

    public void PauseAndCancel()
    {
        _suspended = true;
        _operation?.Cancel();
        _preparationCancellation?.Cancel();
        _thumbnailCancellation?.Cancel();
        _viewportCancellation?.Cancel();
        EndScrub();
        EndRangeDrag();
        Preview?.Pause();
        _sourcePlayer?.Pause();
    }

    public void Resume()
    {
        if (_closed)
        {
            return;
        }

        _suspended = false;
        if (Metadata is null)
        {
            _ = OpenAsync(_openingPosition);
        }
        else
        {
            _ = EnsureWaveformsAsync();
            _ = EnsureThumbnailsAsync();
            if (_effective is null)
            {
                _ = RefreshAsync();
            }
            else
            {
                _ = ReadWaveformsAsync();
            }
        }
    }

    private void Changed()
    {
        _revision++;
        _effective = null;
        InvalidatePreview();
        OnPropertyChanged(nameof(Separate));
        OnPropertyChanged(nameof(IsMp4));
        OnPropertyChanged(nameof(IsMp3));
        foreach (var property in (string[])[nameof(MixWarning), nameof(EffectiveText), nameof(ExportLabel), nameof(SaveLabel),
            nameof(FormatSummary), nameof(SeparateLabel), nameof(MultipleTracks), nameof(SeparateAudioFiles), nameof(OutputSummary)])
        {
            OnPropertyChanged(property);
        }

        NotifyCommands();
        if (!_trimming)
        {
            _ = RefreshAsync();
        }
    }

    internal async Task RefreshAsync()
    {
        if (_closed || _suspended || _trimming || Metadata is null || Exporting)
        {
            return;
        }

        var revision = _revision;
        await WorkAsync(async token =>
        {
            await Task.Delay(150, token);
            if (_trimming || revision != _revision)
            {
                return;
            }

            var range = await _media.ExecuteAsync(Request("range"), null, token);
            token.ThrowIfCancellationRequested();
            if (_trimming || revision != _revision)
            {
                return;
            }

            _effective = range.Range;
            OnPropertyChanged(nameof(EffectiveText));
        });
        if (!_trimming && revision == _revision)
        {
            await ReadWaveformsAsync();
        }
    }

    internal Task EnsureWaveformsAsync()
    {
        if (_closed || _suspended || Metadata is null || _cache is not null)
        {
            return Task.CompletedTask;
        }

        if (_preparation is { IsCompleted: false } pending)
        {
            return _preparationCancellation?.IsCancellationRequested == true ? RetryPreparationAsync(pending) : pending;
        }

        _preparation = PrepareWaveformsAsync();
        return _preparation;
    }

    private async Task RetryPreparationAsync(Task pending)
    {
        await pending;
        await EnsureWaveformsAsync();
    }

    private async Task PrepareWaveformsAsync()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _preparationCancellation = cancel;
        string? folder = null;
        ClipWaveformCache? prepared = null;
        try
        {
            folder = Directory.CreateTempSubdirectory("Encore-waveforms-").FullName;
            var response = await _media.ExecuteAsync(new("prepareWaveforms", Source, Destination: folder), null, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            var descriptor = response.Cache
                ?? throw new IOException("The media worker returned no waveform cache. Reopen the clip creator.");
            prepared = await Task.Run(() => new ClipWaveformCache(folder, descriptor, Source, Metadata!), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_closed)
            {
                return;
            }

            _cache = prepared;
            prepared = null;
            folder = null;
            await ReadWaveformsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                Status = "Waveform preparation failed: " + error.Message;
            }
        }
        finally
        {
            // ExecuteAsync completes only after the owned worker has stopped. Reads finish before disposal deletes files.
            prepared?.Dispose();
            if (folder is not null)
            {
                ClipWaveformCache.DeleteOwnedFolder(folder);
            }

            if (ReferenceEquals(_preparationCancellation, cancel))
            {
                _preparationCancellation = null;
            }
        }
    }

    private async Task ReadWaveformsAsync()
    {
        var generation = ++_viewportRevision;
        _viewportCancellation?.Cancel();
        var cache = _cache;
        if (_closed || _suspended || cache is null)
        {
            return;
        }

        var tracks = Tracks.Where(t => t.Included).Select(t => t.Track.StreamIndex).ToArray();
        // Trimming changes the overlay, not the source peaks in this viewport.
        if (Waveforms.Length == tracks.Length && Waveforms.Select(w => w.StreamIndex).SequenceEqual(tracks)
            && Waveforms.All(w => w.Start == ViewportStart && w.End == ViewportEnd))
        {
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _viewportCancellation = cancel;
        try
        {
            var waveforms = await cache.ReadAsync(ViewportStart, ViewportEnd, tracks, 1600, cancel.Token);
            var current = !cancel.IsCancellationRequested && generation == _viewportRevision && ReferenceEquals(cache, _cache);
            if (!_closed && !_suspended && current)
            {
                Waveforms = waveforms;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            if (!_closed && !_suspended && !cancel.IsCancellationRequested && generation == _viewportRevision)
            {
                Status = "Cannot read waveform cache: " + error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_viewportCancellation, cancel))
            {
                _viewportCancellation = null;
            }
        }
    }

    internal Task EnsureThumbnailsAsync()
    {
        if (_closed || _suspended || Exporting || Metadata is null || Thumbnails.Length != 0 || _thumbnailFailed)
        {
            return Task.CompletedTask;
        }

        if (_thumbnailPreparation is { IsCompleted: false } pending)
        {
            return _thumbnailCancellation?.IsCancellationRequested == true ? RetryThumbnailsAsync(pending) : pending;
        }

        _thumbnailPreparation = PrepareThumbnailsAsync();
        return _thumbnailPreparation;
    }

    private async Task RetryThumbnailsAsync(Task pending)
    {
        await pending;
        await EnsureThumbnailsAsync();
    }

    private async Task PrepareThumbnailsAsync()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _thumbnailCancellation = cancel;
        string? folder = null;
        try
        {
            if (_thumbnailStore is not null)
            {
                var cached = await _thumbnailStore.TryLoadAsync(Source, Duration, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                if (cached is not null)
                {
                    if (!_closed && !_suspended)
                    {
                        Thumbnails = cached;
                    }

                    return;
                }
            }

            folder = Directory.CreateTempSubdirectory("Encore-thumbnails-").FullName;
            var response = await _media.ExecuteAsync(new("prepareThumbnails", Source, Destination: folder), null, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            var descriptor = response.Thumbnails ?? throw new IOException("The media worker returned no thumbnails.");
            var images = await ClipThumbnailCache.LoadAsync(folder, descriptor, Source, Duration, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_thumbnailStore is not null)
            {
                await _thumbnailStore.SaveAsync(folder, descriptor, cancel.Token);
            }

            cancel.Token.ThrowIfCancellationRequested();
            if (!_closed && !_suspended)
            {
                Thumbnails = images;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Optional visual aid: never overwrite range/export errors or block editing on a thumbnail failure.
            if (!_closed && !cancel.IsCancellationRequested)
            {
                _thumbnailFailed = true;
                OnPropertyChanged(nameof(ThumbnailHint));
            }
        }
        finally
        {
            // ExecuteAsync and detached image reads finish before deleting this invocation's scratch.
            if (folder is not null)
            {
                ClipWaveformCache.DeleteOwnedFolder(folder);
            }

            if (ReferenceEquals(_thumbnailCancellation, cancel))
            {
                _thumbnailCancellation = null;
            }
        }
    }

    private async Task StopThumbnailsAsync()
    {
        _thumbnailCancellation?.Cancel();
        if (_thumbnailPreparation is { } preparation)
        {
            await preparation;
        }
    }

    public async Task ExportAsync(string destination)
    {
        if (!CanExport)
        {
            return;
        }

        Exporting = true;
        try
        {
            await WorkAsync(async token =>
            {
                await StopThumbnailsAsync();
                token.ThrowIfCancellationRequested();
                var response = await _media.ExecuteAsync(Request("export") with
                {
                    Destination = destination
                }, new Progress<double>(p => Progress = p), token);
                Status = "Exported to " + response.Output;
            });
        }
        finally
        {
            Exporting = false;
            _ = EnsureThumbnailsAsync();
        }
    }

    public async Task PreviewAsync()
    {
        if (!CanExport)
        {
            return;
        }

        InvalidatePreview();
        string? folder = null;
        var retained = false;
        Exporting = true;
        try
        {
            await WorkAsync(async token =>
            {
                await StopThumbnailsAsync();
                token.ThrowIfCancellationRequested();
                folder = _createPreviewFolder();
                _sourcePlayer?.Pause();
                var response = await _media.ExecuteAsync(Request("preview") with
                {
                    Destination = Path.Combine(folder, "preview.mp4")
                }, new Progress<double>(p => Progress = p), token);
                token.ThrowIfCancellationRequested();
                _effective = response.Range ?? _effective;
                OnPropertyChanged(nameof(EffectiveText));
                var player = new PlayerViewModel(response.Output!, "Selection preview", _newPlayer(), embedded: true);
                ConfigurePlaybackLoop(player, rendered: true);
                _previewFolder = folder;
                _renderedPreview = true;
                OnPropertyChanged(nameof(PreviewLabel));
                retained = true;
                Preview = player;
                await player.OpenAsync();
                token.ThrowIfCancellationRequested();
                player.PlayPauseCommand.Execute(null);
            });
        }
        finally
        {
            Exporting = false;
            _ = EnsureThumbnailsAsync();
            if (!retained && folder is not null)
            {
                DeleteScratch(folder);
            }

            if (_closed)
            {
                InvalidatePreview();
            }
        }
    }

    private ClipMediaRequest Request(string operation) => new(operation, Source, Start, End,
        Tracks: Tracks.Where(t => t.Included).Select(t => t.Track.StreamIndex).ToArray(), Mix: Mix, Format: Format);

    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        _operation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        Busy = true;
        Progress = 0;
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_closed && ReferenceEquals(_operation, operation))
            {
                Status = "Cancelled.";
            }
        }
        catch (Exception error)
        {
            if (!_closed && ReferenceEquals(_operation, operation))
            {
                Status = error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                Busy = false;
            }
        }
    }

    private void InvalidatePreview()
    {
        var old = Preview;
        Preview = _closed ? null : _sourcePlayer;
        if (old is not null && !ReferenceEquals(old, _sourcePlayer))
        {
            old.Dispose();
        }

        _renderedPreview = false;
        OnPropertyChanged(nameof(PreviewLabel));
        if (_previewFolder is { } folder)
        {
            _previewFolder = null;
            DeleteScratch(folder);
        }
    }

    private static void DeleteScratch(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string CreateTempPreviewFolder() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Encore-clip-" + Guid.NewGuid().ToString("N"))).FullName;

    private void IncludeAllTracks(bool included)
    {
        foreach (var track in Tracks)
        {
            track.Included = included;
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _lifetime.Cancel();
        EndScrub();
        EndRangeDrag();
        var cache = _cache;
        _cache = null;
        Thumbnails = [];
        if (cache is not null)
        {
            _ = Task.Run(cache.Dispose);
        }

        InvalidatePreview();
        _sourcePlayer?.Dispose();
        _sourcePlayer = null;
        foreach (var track in Tracks)
        {
            track.Changed -= Changed;
        }

        NotifyCommands();
    }

    private void NotifyRange()
    {
        foreach (var property in (string[])[nameof(Start), nameof(End), nameof(StartText), nameof(EndText), nameof(DurationText),
            nameof(SelectedDurationText), nameof(SelectionLengthText)])
        {
            OnPropertyChanged(property);
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanOpenOptions));
        OnPropertyChanged(nameof(ValidExportName));
        ((RelayCommand)PreviewCommand).RaiseCanExecuteChanged();
        ICommand[] viewportCommands =
        [
            ZoomInCommand, ZoomOutCommand, PanLeftCommand, PanRightCommand, SkipBackCommand, SkipForwardCommand,
        ];
        foreach (var command in viewportCommands)
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }

    public static string FormatTime(double seconds) => FormatTime(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    private static string FormatTime(TimeSpan time)
    {
        time = TimeSpan.FromMilliseconds(Math.Round(time.TotalMilliseconds, MidpointRounding.AwayFromZero));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || hours < 0
            || !TimeSpan.TryParseExact("00:" + parts[1] + ":" + parts[2], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time))
        {
            return false;
        }

        seconds = hours * 3600d + time.TotalSeconds;
        return true;
    }
}
