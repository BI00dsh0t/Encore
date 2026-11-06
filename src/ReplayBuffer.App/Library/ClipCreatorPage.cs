using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Library;

internal sealed class ClipCreatorTrack : Observable
{
    private bool _included = true;
    public ClipCreatorTrack(ClipAudioTrack track) { Track = track; }
    public ClipAudioTrack Track { get; }
    public string Name => Track.Name;
    public bool Included { get => _included; set { if (Set(ref _included, value)) { Changed?.Invoke(); } } }
    public event Action? Changed;
}

/// <summary>One contiguous selection. Viewport and selection are independent source times.</summary>
internal sealed class ClipCreatorPage : Observable, IDisposable
{
    private readonly IClipMediaService _media;
    private readonly Func<IPlayer> _newPlayer;
    private readonly Func<string> _createPreviewFolder;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private PlayerPage? _sourcePlayer;
    private PlayerPage? _preview;
    private string? _previewFolder;
    private bool _renderedPreview;
    private ClipMetadata? _metadata;
    private ClipRange? _effective;
    private ClipWaveform[] _waveforms = [];
    private double _start, _end, _viewportStart, _viewportEnd, _playhead, _progress;
    private bool _mix, _busy, _exporting, _closed;
    private string _format = "mp4", _status = "Opening source…";
    private int _revision;
    private int _sourceCheck;
    private double _openingPosition;

    public ClipCreatorPage(ClipSource source, string title, IClipMediaService media, Func<IPlayer> newPlayer, Func<string>? createPreviewFolder = null)
    {
        Source = source;
        Title = title;
        _media = media;
        _newPlayer = newPlayer;
        _createPreviewFolder = createPreviewFolder ?? (() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ReplayBuffer-clip-" + Guid.NewGuid().ToString("N"))).FullName);
        CancelCommand = new RelayCommand(() => _operation?.Cancel());
        PreviewCommand = new RelayCommand(() => _ = PreviewAsync(), () => CanExport);
        ZoomInCommand = new RelayCommand(() => Zoom(0.5));
        ZoomOutCommand = new RelayCommand(() => Zoom(2));
        PanLeftCommand = new RelayCommand(() => Pan(-0.5));
        PanRightCommand = new RelayCommand(() => Pan(0.5));
        AllTracksCommand = new RelayCommand(() => { foreach (var track in Tracks) { track.Included = true; } });
        NoTracksCommand = new RelayCommand(() => { foreach (var track in Tracks) { track.Included = false; } });
    }

    public ClipSource Source { get; }
    public string Title { get; }
    public Action? SourceInvalidated { get; set; }
    public string PreviewLabel => _renderedPreview
        ? Format == "mp3"
            ? "Rendered selection preview — audio sample boundaries match MP3 export; picture follows the available source frames."
            : "Rendered selection preview — audio and source-frame boundaries match export."
        : "Source video — use Preview selection below to hear the selected tracks and range as exported.";
    public ObservableCollection<ClipCreatorTrack> Tracks { get; } = [];
    public ICommand CancelCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand PanLeftCommand { get; }
    public ICommand PanRightCommand { get; }
    public ICommand AllTracksCommand { get; }
    public ICommand NoTracksCommand { get; }
    public bool IsClosed => _closed;
    public ClipMetadata? Metadata { get => _metadata; private set { Set(ref _metadata, value); OnPropertyChanged(nameof(Duration)); } }
    public double Duration => Metadata?.Duration ?? 0;
    public double Start { get => _start; set => SetRange(value, End); }
    public double End { get => _end; set => SetRange(Start, value); }
    public string StartText { get => FormatTime(Start); set { if (TryTime(value, out var time)) { Start = time; } else { Status = "Enter start as HH:MM:SS.mmm."; } OnPropertyChanged(); } }
    public string EndText { get => FormatTime(End); set { if (TryTime(value, out var time)) { End = time; } else { Status = "Enter end as HH:MM:SS.mmm."; } OnPropertyChanged(); } }
    public string DurationText => $"Selection {FormatTime(TimeSpan.FromSeconds(Math.Max(0, End - Start)))}";
    public string EffectiveText => _effective is { } range
        ? $"{(range.Alignment == "audioSamples48000" ? "Effective audio samples (48 kHz)" : "Effective source frames")}: {FormatTime(range.Start)} – {FormatTime(range.End)}"
        : Format == "mp3" ? "Checking effective audio-sample boundaries…" : "Checking effective source-frame boundaries…";
    public string PlayheadText => FormatTime(Playhead);
    public double Playhead { get => _playhead; private set { if (Set(ref _playhead, value)) { OnPropertyChanged(nameof(PlayheadText)); } } }
    public double ViewportStart => _viewportStart;
    public double ViewportEnd => _viewportEnd;
    public ClipWaveform[] Waveforms { get => _waveforms; private set => Set(ref _waveforms, value); }
    public bool Mix { get => _mix; set { if (Set(ref _mix, value)) { Changed(); } } }
    public bool Separate { get => !Mix; set { if (value) { Mix = false; } } }
    public string Format { get => _format; set { if (value is "mp4" or "mp3" && Set(ref _format, value)) { Changed(); } } }
    public bool IsMp4 { get => Format == "mp4"; set { if (value) { Format = "mp4"; } } }
    public bool IsMp3 { get => Format == "mp3"; set { if (value) { Format = "mp3"; } } }
    public string MixWarning => Mix && Tracks.Count(t => t.Included) > 1
        ? "Mix includes every checked track. Overlapping tracks (such as Everything plus microphone) may count the same sound twice. Normalization and limiting are applied."
        : "Separate tracks retain their names and order. Preview's audio menu can audition each exported stream.";
    public bool Busy { get => _busy; private set { Set(ref _busy, value); NotifyCommands(); } }
    public bool Exporting { get => _exporting; private set { Set(ref _exporting, value); NotifyCommands(); } }
    public bool CanExport => !_closed && Metadata is not null && _effective is not null && !Busy && End > Start && (Format != "mp3" || Tracks.Any(t => t.Included));
    public bool CanEdit => !_closed && !Exporting && Metadata is not null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public PlayerPage? Preview { get => _preview; private set => Set(ref _preview, value); }

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
            _sourcePlayer = new PlayerPage(Source.Path, Title, _newPlayer(), embedded: true);
            Preview = _sourcePlayer;
            await _sourcePlayer.OpenAsync();
            token.ThrowIfCancellationRequested();
            Seek(Math.Clamp(position, 0, Duration));
        });
        if (!_closed && Metadata is not null) { await RefreshAsync(); }
    }

    public void SetRange(double start, double end)
    {
        if (Exporting || _closed || Metadata is null) { return; }
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > Duration || end <= start)
        { Status = "Choose a non-empty range inside the source."; NotifyRange(); return; }
        if (Math.Abs(start - _start) < 0.00000001 && Math.Abs(end - _end) < 0.00000001) { return; }
        (_start, _end) = (start, end);
        NotifyRange();
        Changed();
    }

    public void Nudge(bool end, int milliseconds) => SetRange(Start + (end ? 0 : milliseconds / 1000d), End + (end ? milliseconds / 1000d : 0));
    public void Zoom(double factor)
    {
        if (!CanEdit) { return; }
        var width = Math.Clamp((_viewportEnd - _viewportStart) * factor, Math.Min(0.01, Duration), Duration);
        SetViewport(Math.Clamp(Playhead, _viewportStart, _viewportEnd) - width / 2, width);
    }
    public void Pan(double fraction)
    {
        if (CanEdit) { SetViewport(_viewportStart + (_viewportEnd - _viewportStart) * fraction, _viewportEnd - _viewportStart); }
    }
    private void SetViewport(double start, double width)
    {
        _viewportStart = Math.Clamp(start, 0, Math.Max(0, Duration - width));
        _viewportEnd = Math.Min(Duration, _viewportStart + width);
        Waveforms = [];
        OnPropertyChanged(nameof(ViewportStart)); OnPropertyChanged(nameof(ViewportEnd));
        _ = RefreshAsync();
    }
    public void Seek(double position)
    {
        Playhead = Math.Clamp(position, 0, Duration);
        Preview?.SeekTo(TimeSpan.FromSeconds(_renderedPreview && _effective is { } r ? Math.Clamp(Playhead - r.Start, 0, r.End - r.Start) : Playhead));
    }
    public void Tick()
    {
        if (_closed) { return; }
        if (++_sourceCheck % 10 == 0)
        {
            var valid = false;
            try
            {
                var file = new FileInfo(Source.Path);
                valid = file.Exists && file.Length == Source.Length && file.LastWriteTimeUtc.Ticks == Source.ModifiedUtcTicks;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!valid) { Dispose(); SourceInvalidated?.Invoke(); return; }
        }
        if (Preview is { IsClosed: false } player)
        {
            player.RefreshPosition();
            Playhead = Math.Clamp(player.Position.TotalSeconds + (_renderedPreview ? _effective?.Start ?? 0 : 0), 0, Duration);
        }
    }

    public void PauseAndCancel()
    {
        _operation?.Cancel();
        Preview?.Pause();
        _sourcePlayer?.Pause();
    }

    public void Resume()
    {
        if (_closed) { return; }
        if (Metadata is null) { _ = OpenAsync(_openingPosition); }
        else if (_effective is null || Waveforms.Length == 0 && Tracks.Any(t => t.Included)) { _ = RefreshAsync(); }
    }

    private void Changed()
    {
        _revision++;
        _effective = null;
        Waveforms = [];
        InvalidatePreview();
        OnPropertyChanged(nameof(Separate)); OnPropertyChanged(nameof(IsMp4)); OnPropertyChanged(nameof(IsMp3));
        OnPropertyChanged(nameof(MixWarning)); OnPropertyChanged(nameof(EffectiveText)); NotifyCommands();
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (_closed || Metadata is null || Exporting) { return; }
        var revision = _revision;
        await WorkAsync(async token =>
        {
            await Task.Delay(150, token);
            var range = await _media.ExecuteAsync(Request("range"), null, token);
            token.ThrowIfCancellationRequested();
            if (revision != _revision) { return; }
            _effective = range.Range;
            OnPropertyChanged(nameof(EffectiveText));
            var waveform = await _media.ExecuteAsync(Request("waveform") with { Start = ViewportStart, End = ViewportEnd, Bins = 1600 }, null, token);
            token.ThrowIfCancellationRequested();
            Waveforms = waveform.Waveforms ?? [];
            Status = "Drag either boundary, click to seek, or enter times. Wheel zooms; Shift+wheel pans.";
        });
    }

    public async Task ExportAsync(string destination)
    {
        if (!CanExport) { return; }
        Exporting = true;
        try
        {
            await WorkAsync(async token =>
            {
                var response = await _media.ExecuteAsync(Request("export") with { Destination = destination }, new Progress<double>(p => Progress = p), token);
                Status = "Exported to " + response.Output;
            });
        }
        finally { Exporting = false; }
    }

    public async Task PreviewAsync()
    {
        if (!CanExport) { return; }
        InvalidatePreview();
        string? folder = null;
        var retained = false;
        Exporting = true;
        try
        {
            await WorkAsync(async token =>
            {
                folder = _createPreviewFolder();
                _sourcePlayer?.Pause();
                var response = await _media.ExecuteAsync(Request("preview") with { Destination = Path.Combine(folder, "preview.mp4") }, new Progress<double>(p => Progress = p), token);
                token.ThrowIfCancellationRequested();
                _effective = response.Range ?? _effective;
                OnPropertyChanged(nameof(EffectiveText));
                var player = new PlayerPage(response.Output!, "Selection preview", _newPlayer(), embedded: true);
                _previewFolder = folder;
                _renderedPreview = true;
                OnPropertyChanged(nameof(PreviewLabel));
                retained = true;
                Preview = player;
                await player.OpenAsync();
                token.ThrowIfCancellationRequested();
                player.PlayPauseCommand.Execute(null);
                Status = "Selection preview uses the same effective range and audio rendering as export.";
            });
        }
        finally
        {
            Exporting = false;
            if (!retained && folder is not null) { DeleteScratch(folder); }
            if (_closed) { InvalidatePreview(); }
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
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!_closed && ReferenceEquals(_operation, operation)) { Status = "Cancelled."; } }
        catch (Exception error) { if (!_closed && ReferenceEquals(_operation, operation)) { Status = error.Message; } }
        finally
        {
            if (ReferenceEquals(_operation, operation)) { _operation = null; Busy = false; }
        }
    }

    private void InvalidatePreview()
    {
        var old = Preview;
        Preview = _closed ? null : _sourcePlayer;
        if (old is not null && !ReferenceEquals(old, _sourcePlayer)) { old.Dispose(); }
        _renderedPreview = false;
        OnPropertyChanged(nameof(PreviewLabel));
        if (_previewFolder is { } folder) { _previewFolder = null; DeleteScratch(folder); }
    }
    private static void DeleteScratch(string folder)
    {
        try { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        if (_closed) { return; }
        _closed = true;
        _lifetime.Cancel();
        InvalidatePreview();
        _sourcePlayer?.Dispose(); _sourcePlayer = null;
        foreach (var track in Tracks) { track.Changed -= Changed; }
        NotifyCommands();
    }
    private void NotifyRange()
    {
        foreach (var property in new[] { nameof(Start), nameof(End), nameof(StartText), nameof(EndText), nameof(DurationText), nameof(ViewportStart), nameof(ViewportEnd) }) { OnPropertyChanged(property); }
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanEdit));
        ((RelayCommand)PreviewCommand).RaiseCanExecuteChanged();
    }
    public static string FormatTime(double seconds) => FormatTime(TimeSpan.FromSeconds(Math.Max(0, seconds)));
    private static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    private static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || hours < 0 || !TimeSpan.TryParseExact("00:" + parts[1] + ":" + parts[2], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time)) { return false; }
        seconds = hours * 3600d + time.TotalSeconds;
        return true;
    }
}
