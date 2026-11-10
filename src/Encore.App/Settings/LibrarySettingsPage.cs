using System.Globalization;
using System.Windows;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The Library and Cleanup settings; a change that would delete files asks first, with a preview. UI thread.</summary>
internal sealed class LibrarySettingsPage : SettingsPage
{
    private const long BytesPerGb = 1_000_000_000;
    private const long SecondsPerDay = 86_400;
    private const double MaxGb = 1_000_000;
    private const int MaxDays = 36_500;
    private const string FolderChangeNote = "The Library will list only the new folder. Existing files aren't moved.";

    private readonly IEngineSession _engine;
    private FrameworkElement? _view;
    private CleanupSettings _cleanup = Keys.Cleanup.Default; // saved
    private CleanupSettings _shown = Keys.Cleanup.Default;
    private bool _sizeEnabled;
    private bool _ageEnabled;
    private long _sizeDraft = 100 * BytesPerGb;
    private long _ageDraft = 30 * SecondsPerDay;
    private string? _outputFolder;
    private string? _ringPath;
    private string? _libraryFolder;
    private long _folderBytes;
    private int _folderCount;
    private bool _cleanupBlocked;
    private bool _busy;

    /// <param name="defaultCacheFolder">The engine's default cache folder, which the Clips folder may not contain.</param>
    public LibrarySettingsPage(SettingsSender sender, IEngineSession engine, string? defaultCacheFolder = null)
        : base(sender)
    {
        _engine = engine;
        DefaultCacheFolder = defaultCacheFolder;
        Confirm = ConfirmWithPrompt;
        ClipsFolder = new FolderPicker(Keys.OutputFolder, ops => SendForPickerAsync(ops, ErrorCodes.RingPathInvalid))
        {
            RefusalText = code => code == ErrorCodes.RingPathInvalid ? RewindSettingsPage.ClipsContainsCache : null,
            DialogTitle = "Choose the Clips folder",
            Help = "Where Clips and Recordings are saved. Each game gets its own folder, and Monitor Recordings have theirs.",
            Validate = ValidateClipsFolder,
            Confirm = path => Confirm(path is null
                ? "Use the default folder under Videos as the Clips folder?\n\n" + FolderChangeNote
                : $"Use {path} as the Clips folder?\n\n{FolderChangeNote}"),
        };
    }

    public override string Title => "Storage and Cleanup";

    /// <summary>The engine's default cache folder, used while <c>ringPath</c> is unset.</summary>
    public string? DefaultCacheFolder { get; }

    /// <summary>The Clips folder (<c>outputFolder</c>), as a folder picker.</summary>
    public FolderPicker ClipsFolder { get; }

    /// <summary>Asks the user a Yes/No question; true is Yes. Settable for tests.</summary>
    internal Func<string, bool> Confirm { get; set; }

    /// <summary>The folder the Library lists: the engine's, else the setting, else the default.</summary>
    public string FolderSizeText => string.Create(
        CultureInfo.InvariantCulture,
        $"{RewindSettingsPage.FormatBytes(_folderBytes)} in {_folderCount} {(_folderCount == 1 ? "file" : "files")}");

    /// <summary>The note shown while Cleanup is blocked by a rebuilt index; null otherwise.</summary>
    public string? CleanupBlockedText => _cleanupBlocked
        ? "Cleanup is off until the rebuilt index is acknowledged on the Library tab."
        : null;

    /// <summary>Whether the size limit is on (<c>cleanup.maxBytes</c> not null).</summary>
    public bool SizeLimitEnabled
    {
        get => _sizeEnabled;
        set
        {
            if (value == _sizeEnabled)
            {
                return;
            }

            _ = ChangeLimitsAsync(_shown with { MaxBytes = value ? _sizeDraft : null });
        }
    }

    /// <summary>The size limit in GB (10^9 bytes); the draft while the limit is off.</summary>
    public string SizeLimitText
    {
        get => FormatGb(_shown.MaxBytes ?? _sizeDraft);
        set
        {
            if (!TryParseGb(value, out var bytes))
            {
                Refuse("The size limit must be a number of GB above 0 and up to 1 000 000.", nameof(SizeLimitText));
                return;
            }

            if (!_sizeEnabled)
            {
                _sizeDraft = bytes;
                OnPropertyChanged(nameof(SizeLimitText));
                return;
            }

            _ = ChangeLimitsAsync(_shown with { MaxBytes = bytes });
        }
    }

    /// <summary>Whether the age limit is on (<c>cleanup.maxAgeSeconds</c> not null).</summary>
    public bool AgeLimitEnabled
    {
        get => _ageEnabled;
        set
        {
            if (value == _ageEnabled)
            {
                return;
            }

            _ = ChangeLimitsAsync(_shown with { MaxAgeSeconds = value ? _ageDraft : null });
        }
    }

    /// <summary>The age limit in whole days; the draft while the limit is off.</summary>
    public string AgeLimitText
    {
        get => FormatDays(_shown.MaxAgeSeconds ?? _ageDraft);
        set
        {
            if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days is < 1 or > MaxDays)
            {
                Refuse("The age limit must be a whole number of days, 1 to 36 500.", nameof(AgeLimitText));
                return;
            }

            var seconds = days * SecondsPerDay;
            if (!_ageEnabled)
            {
                _ageDraft = seconds;
                OnPropertyChanged(nameof(AgeLimitText));
                return;
            }

            _ = ChangeLimitsAsync(_shown with { MaxAgeSeconds = seconds });
        }
    }

    /// <summary>Takes the snapshot's Library folder, items and Cleanup block. Called before <see cref="SettingsPage.Render"/>.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_libraryFolder != state.LibraryFolder)
        {
            _libraryFolder = state.LibraryFolder;
            ClipsFolder.Show(_outputFolder, _libraryFolder ?? "");
        }

        var bytes = state.Library.Sum(i => i.Size);
        var count = state.Library.Count;
        if (bytes != _folderBytes || count != _folderCount)
        {
            (_folderBytes, _folderCount) = (bytes, count);
            OnPropertyChanged(nameof(FolderSizeText));
        }

        if (_cleanupBlocked != state.CleanupBlocked)
        {
            _cleanupBlocked = state.CleanupBlocked;
            OnPropertyChanged(nameof(CleanupBlockedText));
        }
    }

    protected override FrameworkElement CreateView() => _view = new LibrarySettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _cleanup = SettingsValues.Global(document, Keys.Cleanup);
        if (!_busy)
        {
            ShowLimits(_cleanup);
        }

        _ringPath = SettingsValues.Global(document, Keys.RingPath);
        var folder = SettingsValues.Global(document, Keys.OutputFolder);
        _outputFolder = folder;
        ClipsFolder.Show(folder, _libraryFolder ?? "");
    }

    private static bool Lowers(long? next, long? current) => next is { } n && (current is null || current > n);

    private static string FormatGb(long bytes) => (bytes / (double)BytesPerGb).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatDays(long seconds) =>
        Math.Max(1, seconds / SecondsPerDay).ToString(CultureInfo.InvariantCulture);

    private static bool TryParseGb(string? text, out long bytes)
    {
        bytes = 0;
        var trimmed = text?.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out gb))
        {
            return false;
        }

        if (!double.IsFinite(gb) || gb <= 0 || gb > MaxGb)
        {
            return false;
        }

        bytes = (long)Math.Round(gb * BytesPerGb);
        return bytes >= 1;
    }

    private static string PlanText(LibraryCleanupPlan plan)
    {
        var now = plan.Count == 0
            ? "Nothing would be deleted now."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Cleanup would delete {plan.Count} {(plan.Count == 1 ? "file" : "files")} ({RewindSettingsPage.FormatBytes(plan.Bytes)}) now.");
        return now
            + "\n\nCleanup deletes permanently: files don't go to the Recycle Bin."
            + " Favourites and files being written are never deleted."
            + "\n\nApply the new limits?";
    }

    // Sends the new limits: at once when no limit is turned on or lowered, else after a preview and a Yes.
    private async Task ChangeLimitsAsync(CleanupSettings next)
    {
        if (_busy)
        {
            ShowLimits(_shown);
            return;
        }

        if (next == _cleanup)
        {
            ShowLimits(_cleanup);
            return;
        }

        if (!Lowers(next.MaxBytes, _cleanup.MaxBytes) && !Lowers(next.MaxAgeSeconds, _cleanup.MaxAgeSeconds))
        {
            _cleanup = next;
            ShowLimits(next);
            _ = SendAsync([SettingsValues.SetGlobal(Keys.Cleanup, next)]);
            return;
        }

        var basis = _cleanup;
        _busy = true;
        ShowLimits(next);
        try
        {
            LibraryCleanupPlan plan;
            try
            {
                plan = await _engine.SendAsync(LibraryMessages.CleanupPreview, new LibraryCleanupPreview(next.MaxBytes, next.MaxAgeSeconds));
            }
            catch (ErrorCodeException e)
            {
                Message = $"Couldn't check what Cleanup would delete ({e.Code}): {e.Message}";
                ShowLimits(_cleanup);
                return;
            }
#pragma warning disable CA1031 // Any failure of the preview leaves the limits as they were.
            catch (Exception e)
#pragma warning restore CA1031
            {
                Message = $"Couldn't check what Cleanup would delete: {e.Message}";
                ShowLimits(_cleanup);
                return;
            }

            if (!Confirm(PlanText(plan)))
            {
                return;
            }

            // A newer cleanup arrived while the prompt was open; sending next whole would overwrite it.
            if (_cleanup != basis)
            {
                Message = "The Cleanup limits changed elsewhere, so this page reloaded. Make the change again if you still want it.";
                return;
            }

            _cleanup = next;
            await SendAsync([SettingsValues.SetGlobal(Keys.Cleanup, next)]);
        }
        finally
        {
            // Renders skipped while busy; show what is saved now (a failed or refused send included).
            _busy = false;
            ShowLimits(_cleanup);
        }
    }

    // Shows the limits as the checkboxes and text boxes; a set limit also becomes its draft.
    private void ShowLimits(CleanupSettings cleanup)
    {
        _shown = cleanup;
        _sizeEnabled = cleanup.MaxBytes is not null;
        _ageEnabled = cleanup.MaxAgeSeconds is not null;
        _sizeDraft = cleanup.MaxBytes ?? _sizeDraft;
        _ageDraft = cleanup.MaxAgeSeconds ?? _ageDraft;
        foreach (var name in (string[])[nameof(SizeLimitEnabled), nameof(SizeLimitText), nameof(AgeLimitEnabled), nameof(AgeLimitText)])
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The Clips folder's refusals: it contains the cache folder, or it isn't writable.</summary>
    private string? ValidateClipsFolder(string path)
    {
        if ((_ringPath ?? DefaultCacheFolder) is { Length: > 0 } cache && RewindSettingsPage.IsInside(cache, path))
        {
            return RewindSettingsPage.ClipsContainsCache;
        }

        return FolderProbe.CanWrite(path) ? null : RewindSettingsPage.CantWrite;
    }

    // Shows the rendered value again, with why the entry wasn't sent.
    private void Refuse(string message, string property)
    {
        Message = message;
        OnPropertyChanged(property);
    }

    private bool ConfirmWithPrompt(string message) => Prompts.YesNo(_view, message, Title, MessageBoxImage.Warning);
}
