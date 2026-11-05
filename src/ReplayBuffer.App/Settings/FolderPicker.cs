using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>
/// The folder picker (story 10.9, epic 10's EXPERIENCE.md "Folder picker"): one nullable folder key,
/// shown read-only with Browse…, Open in Explorer and Use default, the drive's free space, and a danger
/// line when the last choice was refused. A pick sends the key's Global <c>set</c> at once; Use default
/// sends <c>remove</c>; a cancelled dialog sends nothing. No path is ever typed. Members run on the UI thread.
/// The file variant (<see cref="IsFile"/>) opens a file dialog instead and has Browse… only.
/// </summary>
internal sealed class FolderPicker : Observable
{
    private readonly SettingKey<string?> _key;
    private readonly Func<IReadOnlyList<SettingsOp>, Task<string?>> _send;
    private string? _value;
    private string _defaultPath = "";
    private string? _problem;
    private bool _isEnabled = true;

    /// <param name="key">The nullable folder key it sets and removes.</param>
    /// <param name="send">Sends the ops, as the hosting page's <c>SendAsync</c>; returns a refusal's error code, or null.</param>
    public FolderPicker(SettingKey<string?> key, Func<IReadOnlyList<SettingsOp>, Task<string?>> send)
    {
        _key = key;
        _send = send;
        BrowseCommand = new RelayCommand(Browse, () => IsEnabled);
        OpenCommand = new RelayCommand(Open, () => IsEnabled && FolderExists);
        UseDefaultCommand = new RelayCommand(UseDefault, () => IsEnabled && CanUseDefault);
        Dialog = ShowFolderDialog;
    }

    /// <summary>The help line under the buttons.</summary>
    public string Help { get; init; } = "";

    /// <summary>The caption shown in place of the help while the picker is disabled, e.g. "Used only in Disk mode".</summary>
    public string DisabledCaption { get; init; } = "";

    /// <summary>The Windows dialog's title.</summary>
    public string DialogTitle { get; init; } = "Choose a folder";

    /// <summary>The file variant: Browse… opens a file dialog with <see cref="FileFilter"/>, and there's no Open or Use default.</summary>
    public bool IsFile { get; init; }

    /// <summary>Whether Open in Explorer shows: not for the file variant.</summary>
    public bool IsFolder => !IsFile;

    public string FileFilter { get; init; } = "Programs (*.exe)|*.exe";

    /// <summary>Why a chosen folder can't be used, in plain words, or null when it can. Checked before anything is sent.</summary>
    public Func<string, string?> Validate { get; set; } = _ => null;

    /// <summary>The plain words for an engine refusal's error code, or null to leave it to the page's error bar.</summary>
    public Func<string, string?> RefusalText { get; set; } = _ => null;

    /// <summary>Asks before a change (null: back to the default); true goes ahead. By default, always.</summary>
    public Func<string?, bool> Confirm { get; set; } = _ => true;

    /// <summary>The Windows dialog, opened at a folder; returns the choice, or null when cancelled. Tests replace it.</summary>
    public Func<string, string?> Dialog { get; set; }

    /// <summary>Opens a folder in Explorer. Tests replace it.</summary>
    public Action<string> OpenFolder { get; set; } = folder =>
    {
        using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
    };

    /// <summary>Whether a folder exists; Open in Explorer is off while it doesn't. Tests replace it.</summary>
    public Func<string, bool> Exists { get; set; } = Directory.Exists;

    /// <summary>The free bytes of the drive holding a path, or null when unknown. Tests replace it.</summary>
    public Func<string, long?> FreeBytes { get; set; } = DriveFree;

    /// <summary>The key's value; null is the default.</summary>
    public string? Value => _value;

    /// <summary>The folder in use: the value, or the default folder.</summary>
    public string ShownPath => _value ?? _defaultPath;

    /// <summary>The path as shown: "Default ({path})" when the key is unset.</summary>
    public string PathText => _value ?? (_defaultPath.Length > 0 ? $"Default ({_defaultPath})" : "Default");

    public bool CanUseDefault => !IsFile && _value is not null;

    public bool FolderExists => ShownPath.Length > 0 && Exists(ShownPath);

    /// <summary>The Open in Explorer tooltip: why it's off while the folder doesn't exist.</summary>
    public string OpenTip => FolderExists ? "Open this folder in Explorer" : "The folder doesn't exist yet.";

    /// <summary>"212 GB free on C:", or empty when unknown.</summary>
    public string FreeSpaceText
    {
        get
        {
            if (IsFile || ShownPath.Length == 0 || !Path.IsPathFullyQualified(ShownPath))
            {
                return "";
            }

            var root = Path.GetPathRoot(ShownPath)?.TrimEnd('\\') ?? "";
            return FreeBytes(ShownPath) is { } free ? $"{FormatSize(free)} free on {root}" : "";
        }
    }

    /// <summary>The help, or the disabled caption while disabled.</summary>
    public string Caption => IsEnabled ? Help : DisabledCaption;

    /// <summary>Why the last choice wasn't used; null when it was.</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (Set(ref _problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => Problem is not null;

    /// <summary>The cache folder is used only in Disk mode: off, the whole row is disabled.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(Caption));
                RaiseCommands();
            }
        }
    }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand UseDefaultCommand { get; }

    /// <summary>Shows the key's value and the default folder; also refreshes the free space and Open's state.</summary>
    public void Show(string? value, string defaultPath)
    {
        _value = value;
        _defaultPath = defaultPath ?? "";
        Refresh();
    }

    /// <summary>Re-reads whether the folder exists and its drive's free space, as when the page opens.</summary>
    public void Refresh()
    {
        foreach (var name in (string[])[nameof(Value), nameof(ShownPath), nameof(PathText), nameof(CanUseDefault), nameof(FolderExists), nameof(OpenTip), nameof(FreeSpaceText)])
        {
            OnPropertyChanged(name);
        }

        RaiseCommands();
    }

    /// <summary>
    /// A chosen folder: refused with <see cref="Problem"/> (the old path kept) when it isn't a full path or
    /// <see cref="Validate"/> says why not; otherwise sends the key's <c>set</c>, after <see cref="Confirm"/>.
    /// </summary>
    public void Choose(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            Problem = "Choose a whole folder, such as D:\\Clips.";
            return;
        }

        path = Path.TrimEndingDirectorySeparator(path);
        if (Validate(path) is { } refusal)
        {
            Problem = refusal;
            return;
        }

        Problem = null;
        if (string.Equals(path, _value, StringComparison.OrdinalIgnoreCase) || !Confirm(path))
        {
            return;
        }

        _ = SendAsync([SettingsValues.SetGlobal(_key, path)]);
    }

    /// <summary>"212 GB", "1.8 TB": decimal units, one decimal under 10 and from TB.</summary>
    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e12:0.0} TB"),
        >= 10_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0} GB"),
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB"),
    };

    private static long? DriveFree(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void Browse()
    {
        if (Dialog(ShownPath) is { } chosen)
        {
            Choose(chosen);
        }
    }

    private void Open()
    {
        try
        {
            OpenFolder(ShownPath);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Problem = "Explorer couldn't open that folder.";
        }
    }

    private void UseDefault()
    {
        if (_value is null || !Confirm(null))
        {
            return;
        }

        Problem = null;
        _ = SendAsync([SettingsOp.RemoveGlobal(_key.Name)]);
    }

    // An engine refusal the picker words shows on its danger line, and the old path stays.
    private async Task SendAsync(IReadOnlyList<SettingsOp> ops)
    {
        if (await _send(ops) is { } code && RefusalText(code) is { } text)
        {
            Problem = text;
        }
    }

    private void RaiseCommands()
    {
        BrowseCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        UseDefaultCommand.RaiseCanExecuteChanged();
    }

    private string? ShowFolderDialog(string initial)
    {
        var owner = Application.Current?.MainWindow;
        if (IsFile)
        {
            var file = new OpenFileDialog { Title = DialogTitle, Filter = FileFilter, CheckFileExists = true };
            return file.ShowDialog(owner) == true ? file.FileName : null;
        }

        var dialog = new OpenFolderDialog { Title = DialogTitle };
        if (initial.Length > 0 && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    /// <summary>Whether ReplayBuffer can write to a folder: a missing one is created later, so it counts if its nearest existing parent is writable.</summary>
    internal static bool CanWrite(string folder)
    {
        try
        {
            var probe = folder;
            while (!Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
                if (probe is null)
                {
                    return false;
                }
            }

            var file = Path.Combine(probe, ".replaybuffer-write-check-" + Guid.NewGuid().ToString("N"));
            using (File.Create(file, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
