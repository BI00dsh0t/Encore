using System.Runtime.CompilerServices;
using System.Windows;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The Clips settings, each edit sent at once; with a profile id it edits that game's overrides.</summary>
internal sealed class RewindSettingsViewModel : SettingsViewModel
{
    private readonly string? _defaultCacheFolder;
    private int _bufferWindow = Keys.BufferWindow.Default;
    private ReplayStorage _storage = Keys.ReplayStorage.Default;
    private string? _ringPath = Keys.RingPath.Default;
    private int _saveCooldown = Keys.SaveCooldown.Default;
    private StorageEstimate? _estimate;
    private bool _bufferWindowUsesGlobal;
    private FolderPicker? _cacheFolder;
    private string? _clipsFolder;
    private string? _libraryFolder;
    private HotkeyBinding? _saveKey;

    public RewindSettingsViewModel(SettingsSender sender, string? profileId = null, string? defaultCacheFolder = null)
        : base(sender)
    {
        ProfileId = profileId;
        _defaultCacheFolder = defaultCacheFolder;
        _bufferWindowUsesGlobal = profileId is not null;
        Video = new VideoQualityViewModel(profileId, SendAsync, message => Message = message);
    }

    public override string Title => "Clips";

    /// <summary>The page's intro: what it controls, with the Save Hotkey.</summary>
    public string Intro
    {
        get
        {
            var key = _saveKey is { } binding ? HotkeyText.Describe(binding) : "the Save Clip key";
            return $"Encore keeps the last part of your game ready, so {key} can save it as a Clip.";
        }
    }

    /// <summary>The video quality: codec, resolution, frame rate and bitrate.</summary>
    public VideoQualityViewModel Video { get; }

    /// <summary>The cache folder (<c>ringPath</c>): enabled only under Disk.</summary>
    public FolderPicker CacheFolder => _cacheFolder ??= new FolderPicker(Keys.RingPath, ops => SendForPickerAsync(ops, ErrorCodes.RingPathInvalid))
    {
        RefusalText = code => code == ErrorCodes.RingPathInvalid ? FolderRules.CacheInsideClips : null,
        DialogTitle = "Choose the cache folder",
        DisabledCaption = "Used only in Disk mode",
        Validate = ValidateCacheFolder,
    };

    /// <summary>The cache folder's help: what it holds, with the Clip length.</summary>
    public string CacheHelp =>
        $"In Disk mode, the last {HotkeyText.ClipLength(_bufferWindow)} of gameplay is kept here in one file. It doesn't grow.";

    /// <summary>The Memory option's line, with the estimate.</summary>
    public string MemoryLine => StorageEstimateText.MemoryLine(_estimate);

    /// <summary>The Disk option's line, with the estimate.</summary>
    public string DiskLine => StorageEstimateText.DiskLine(_estimate);

    /// <summary>The Game Profile whose overrides this page edits; null on the Global page.</summary>
    public string? ProfileId { get; }

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
            _ = SendAsync([SettingsValues.UseGlobal(Keys.BufferWindow, ProfileId!, value, _bufferWindow)]);
        }
    }

    public bool CanEditBufferWindow => IsGlobal || !BufferWindowUsesGlobal;

    /// <summary><c>bufferWindow</c>, in s; at least 1.</summary>
    public string BufferWindowText
    {
        get => SettingsText.Whole(_bufferWindow);
        set
        {
            if (!CanEditBufferWindow)
            {
                OnPropertyChanged();
                return;
            }

            const string Refusal = "The Clip length must be a whole number of seconds, 1 or more.";
            SendWhole(value, ref _bufferWindow, seconds => seconds >= 1, Keys.BufferWindow, ProfileId, Refusal);
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
        get => SettingsText.Whole(_saveCooldown);
        set
        {
            const string Refusal = "Time between saves must be a whole number of seconds, 0 or more.";
            SendWhole(value, ref _saveCooldown, seconds => seconds >= 0, Keys.SaveCooldown, null, Refusal);
        }
    }

    /// <summary>The estimate of the Global settings, e.g. "Estimated RAM use: 1.6 GB".</summary>
    public string EstimateText => StorageEstimateText.Summary(_estimate);

    /// <summary>The warning when the estimate is over the free bytes; null otherwise. It blocks nothing.</summary>
    public string? EstimateWarning => StorageEstimateText.Warning(_estimate);

    /// <summary>Takes the snapshot's video options and storage estimate. Called before <see cref="SettingsViewModel.Render"/>.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Video.ShowOptions(state.VideoOptions);
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

    // A game's Clip length and Video quality show in their own editors on its page, so only the main page has a view.
    protected override FrameworkElement CreateView() => new RewindSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        if (IsProfile)
        {
            ShowBufferWindowUsesGlobal(!SettingsValues.HasOverride(document, Keys.BufferWindow, ProfileId!));
        }

        _bufferWindow = SettingsValues.Resolve(document, Keys.BufferWindow, ProfileId);
        _storage = SettingsValues.Global(document, Keys.ReplayStorage);
        _ringPath = SettingsValues.Global(document, Keys.RingPath);
        _clipsFolder = SettingsValues.Global(document, Keys.OutputFolder);
        _saveKey = SettingsValues.Global(document, Keys.Hotkeys).Save;
        if (IsGlobal)
        {
            CacheFolder.Show(_ringPath, _defaultCacheFolder ?? "");
            CacheFolder.IsEnabled = _storage == ReplayStorage.Disk;
        }

        _saveCooldown = SettingsValues.Global(document, Keys.SaveCooldown);
        foreach (var name in (string[])[nameof(BufferWindowText), nameof(IsMemory), nameof(IsDisk), nameof(SaveCooldownText),
            nameof(Intro), nameof(CacheHelp)])
        {
            OnPropertyChanged(name);
        }

        Video.Render(document);
    }

    /// <summary>The cache folder's refusals: inside the Clips folder, or not writable.</summary>
    private string? ValidateCacheFolder(string path)
    {
        if ((_clipsFolder ?? _libraryFolder) is { Length: > 0 } clips && FolderRules.IsInside(path, clips))
        {
            return FolderRules.CacheInsideClips;
        }

        return FolderProbe.CanWrite(path) ? null : FolderRules.CantWrite;
    }

    private void ShowBufferWindowUsesGlobal(bool value)
    {
        if (Set(ref _bufferWindowUsesGlobal, value, nameof(BufferWindowUsesGlobal)))
        {
            OnPropertyChanged(nameof(CanEditBufferWindow));
        }
    }

    // A whole-number text box: a bad entry is refused, a new value is kept and sent.
    private void SendWhole(
        string? text,
        ref int field,
        Func<int, bool> isValid,
        SettingKey<int> key,
        string? scope,
        string refusal,
        [CallerMemberName] string? property = null)
    {
        if (!SettingsText.TryParseWhole(text, out var value) || !isValid(value))
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
}
